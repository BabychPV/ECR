using System.Data;
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// <see cref="ISheetEditGate"/> на <c>sp_getapplock</c> з власником
/// <c>Transaction</c>.
/// </summary>
/// <remarks>
/// ⚠ Чому applock, а не <c>UPDLOCK, HOLDLOCK</c> на <c>wf.ApprovalState</c>.
/// Рядка стану може ще НЕ БУТИ — він з'являється при першій дії над аркушем
/// (<c>WorkflowStore.GetOrCreateAsync</c>), а перше подання якраз і є такою дією.
/// Блокування неіснуючого рядка — це діапазонне блокування ключа на індексі,
/// і його ширина залежить від сусідніх ключів, тобто від чужих аркушів. Applock
/// блокує рівно один рядковий ключ «документ × аркуш × період» і нічого поруч.
///
/// ⚠ Порядок блокувань. Обидві сторони беруть applock ПЕРШОЮ дією своєї
/// транзакції запису (правка — до <c>MERGE doc.CellValue</c>, подання — до
/// будь-якого читання). Подання далі пише лише <c>calc.SubmissionSnapshot</c>,
/// <c>wf.ApprovalState</c>, <c>wf.ApprovalEvent</c> і <c>rpt.*</c> — таблиці, яких
/// правка не торкається, — тож, тримаючи виняткове блокування, воно не чекає на
/// жоден ресурс правки, і цикл очікування скластися не може. Імпорт Excel, що
/// в одній транзакції бере спільні блокування кількох аркушів, теж не утворює
/// циклу: подання тримає ОДИН ключ і на імпорт не чекає.
///
/// ⚠ Перерахунок (<c>RecalculationService</c>) теж бере кілька спільних — і
/// бере їх у стабільному порядку ключа (у межах прогону — <c>SheetDefId</c> за
/// зростанням): черга блокувань FIFO, спільний запит стає за винятковим, що вже
/// чекає, і два багатоаркушеві власники в різному порядку разом із двома
/// поданнями в черзі склали б цикл.
/// </remarks>
public sealed class SheetEditGate(EcrDbContext db, SheetEditGatePolicy? policy = null) : ISheetEditGate
{
    /// <summary>Режим <c>sp_getapplock</c> для правки й перерахунку.</summary>
    private const string SharedMode = "Shared";

    /// <summary>Режим <c>sp_getapplock</c> для подання.</summary>
    private const string ExclusiveMode = "Exclusive";

    /// <summary><c>sp_getapplock</c>: очікування вичерпано.</summary>
    private const int LockTimedOut = -1;

    /// <summary><c>sp_getapplock</c>: запит обрано жертвою дедлоку.</summary>
    private const int LockDeadlockVictim = -3;

    private readonly TimeSpan _lockTimeout = (policy ?? SheetEditGatePolicy.Default).LockTimeout;

    /// <inheritdoc />
    public async Task<DocumentStatus> EnterEditAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        await AcquireAsync(documentId, sheetDefId, periodKey, SharedMode, ct).ConfigureAwait(false);

        // ⛔ Стан читається ПІСЛЯ блокування і окремим запитом: під RCSI знімок
        // береться на початку ОПЕРАТОРА, тож цей оператор бачить подання, яке
        // зафіксувалося, поки ми чекали на блокування.
        var status = await db.ApprovalStates
            .AsNoTracking()
            .Where(a => a.DocumentId == documentId
                        && a.SheetDefId == sheetDefId
                        && a.PeriodKey == periodKey.Value)
            .Select(a => (DocumentStatus?)a.Status)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return status ?? DocumentStatus.Draft;
    }

    /// <inheritdoc />
    public Task EnterSubmitAsync(long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
        => AcquireAsync(documentId, sheetDefId, periodKey, ExclusiveMode, ct);

    private async Task AcquireAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, string mode, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
                          ?? throw new InvalidOperationException(
                              "Блокування аркуша береться лише всередині транзакції: " +
                              "поза нею воно звільнилося б одразу і нічого не захистило б.");

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "sp_getapplock";

        var resource = string.Create(
            CultureInfo.InvariantCulture, $"ecr:sheet-edit:{documentId}:{sheetDefId}:{periodKey.Value}");

        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = mode });
        command.Parameters.Add(new SqlParameter("@LockOwner", SqlDbType.VarChar, 32) { Value = "Transaction" });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)
        {
            Value = (int)Math.Min(int.MaxValue, _lockTimeout.TotalMilliseconds),
        });
        var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(result);

        // ⚠ Таймаут команди — більший за таймаут блокування: інакше клієнт
        // обірвав би очікування раніше, ніж сервер відповів би кодом відмови.
        command.CommandTimeout = (int)Math.Ceiling(_lockTimeout.TotalSeconds) + 15;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // 0 — узято одразу, 1 — узято після очікування; від'ємне — відмова.
        var code = (int)result.Value!;

        if (code is LockTimedOut or LockDeadlockVictim)
        {
            throw Busy(documentId, sheetDefId, periodKey, mode, code);
        }

        if (code < 0)
        {
            // ⚠ Решта від'ємних кодів (-2 — запит скасовано, -999 — помилка
            // параметрів) — не «аркуш зайнятий», а збій, і вдавати його
            // зрозумілою відмовою означало б сховати дефект.
            throw new InvalidOperationException(
                $"Не вдалося взяти блокування аркуша «{resource}» ({mode}): sp_getapplock повернув {code}.");
        }
    }

    /// <summary>Відмова «аркуш зайнятий» — <c>409 ECR-DOC-4091</c>.</summary>
    /// <remarks>
    /// ⛔ Що було. Тайм-аут очікування віддавався голим
    /// <c>InvalidOperationException</c>, тобто <c>500 ECR-SYS-0500</c> «зверніться
    /// до адміністратора» — на стан, який за секунду минає сам: подання ЦЬОГО
    /// аркуша ще не закінчилось (<c>SheetEditGateTimeoutTests</c>).
    ///
    /// ⚠ 409, а не 423 чи 503. <c>423</c> у цьому API вже означає заблокований
    /// обліковий запис (<c>ECR-AUTH-0423</c>), <c>503</c> — несправний сервіс; а тут
    /// запит розминувся з чужою дією над ТИМ САМИМ ресурсом — те саме сімейство, що
    /// <c>ECR-CELL-0409</c>, і повтор за мить його знімає.
    ///
    /// ⚠ <see cref="ConcurrencyConflictException"/>, а не <c>TimeoutException</c>:
    /// останній стратегія повторів EF вважає транзієнтним і повторила б усе тіло
    /// транзакції, тобто очікування помножилося б на число повторів.
    ///
    /// ⚠ Жертва дедлоку (-3) — та сама відмова: для людини це той самий стан
    /// «зайнято, повторіть», а транзакція однаково відкочується.
    /// </remarks>
    private static ConcurrencyConflictException Busy(
        long documentId, int sheetDefId, PeriodKey periodKey, string mode, int code)
    {
        // ⚠ Хто ЧЕКАВ, той і читає відмову: правка чи перерахунок (спільне)
        // чекали на подання, а подання (виняткове) — на правку чи перерахунок.
        var messageKey = mode == ExclusiveMode
            ? "err.ECR-DOC-4091.sheetBeingEdited"
            : "err.ECR-DOC-4091.sheetBeingSubmitted";

        return new ConcurrencyConflictException(
            ErrorCodes.SheetBusy,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Аркуш {sheetDefId} документа {documentId} за період {periodKey.Value} зайнятий " +
                $"({mode}, sp_getapplock = {code}): повторіть дію за мить."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
                ["periodKey"] = periodKey.Value.ToString(CultureInfo.InvariantCulture),
            });
    }
}

/// <summary>Налаштування <see cref="SheetEditGate"/>.</summary>
/// <param name="LockTimeout">Скільки чекати на блокування аркуша.</param>
/// <remarks>
/// ⚠ Подання триває секунди (валідація + зріз). Тридцять секунд — з великим
/// запасом; довше — це вже не черга, а щось зависле, і тоді чесніше відмовити,
/// ніж тримати запит нескінченно. Задається <c>Database:SheetLockTimeoutSeconds</c>;
/// тест підміняє запис у контейнері, щоб не чекати пів хвилини.
/// </remarks>
public sealed record SheetEditGatePolicy(TimeSpan LockTimeout)
{
    /// <summary>Типове очікування, секунд.</summary>
    public const int DefaultLockTimeoutSeconds = 30;

    /// <summary>Типові налаштування.</summary>
    public static SheetEditGatePolicy Default { get; } = new(TimeSpan.FromSeconds(DefaultLockTimeoutSeconds));
}
