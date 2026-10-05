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
/// будь-якого читання). Подання далі пише <c>calc.SubmissionSnapshot</c>,
/// <c>wf.ApprovalState</c>, <c>wf.ApprovalEvent</c>, <c>rpt.*</c> і — перераховуючи
/// свій аркуш (<c>ISubmitRecalculation</c>) — <c>doc.CellValue</c>/<c>aud.CellChange</c>
/// лише СВОГО аркуша. Рядки свого аркуша пише тільки той, хто тримає його
/// блокування, а кожен писар бере applock ДО першого запису й до будь-якого
/// рядкового блокування, — тож, тримаючи виняткове, подання не чекає на жоден
/// ресурс, який хтось тримав би, чекаючи на подання, і цикл скластися не може.
///
/// ⚠ Перерахунок (<c>RecalculationService</c>) та імпорт Excel
/// (<c>ExcelImporter.ApplyAsync</c>, заздалегідь, першою дією транзакції книги)
/// беруть кілька спільних — і беруть їх у стабільному порядку ключа (у межах прогону — <c>SheetDefId</c> за
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

    /// <summary>Ім'я ресурсу <c>sp_getapplock</c> аркуша «документ × аркуш × період».</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <remarks>
    /// ⛔ Одне ім'я на всіх, хто пише аркуш повз <c>PatchCellsHandler</c> (видалення рядків подій —
    /// <c>SourceEventSyncJob</c>, L3-04): інший рядок ресурсу був би іншим блокуванням, і подання
    /// його не чекало б.
    /// </remarks>
    /// <returns>Рядок ресурсу.</returns>
    public static string ResourceOf(long documentId, int sheetDefId, int periodKey)
        => string.Create(CultureInfo.InvariantCulture, $"ecr:sheet-edit:{documentId}:{sheetDefId}:{periodKey}");

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

    /// <inheritdoc />
    public async Task<int?> EnterStructureAsync(long documentId, bool exclusive, CancellationToken ct)
    {
        var mode = exclusive ? ExclusiveMode : SharedMode;
        var resource = string.Create(CultureInfo.InvariantCulture, $"ecr:doc-structure:{documentId}");
        var (code, version) = await GetAppLockAsync(resource, mode, documentId, ct).ConfigureAwait(false);

        // ⚠ Хто чекав, той і читає відмову: перенос — на записи документа, запис — на перенос.
        ThrowIfRefused(code, resource, mode, () => DocumentBusy(
            documentId, mode, code,
            exclusive ? "err.ECR-DOC-4091.documentBeingEdited" : "err.ECR-DOC-4091.structureChanging"));

        return version;
    }

    /// <inheritdoc />
    public async Task EnterHeaderAsync(long documentId, bool exclusive, CancellationToken ct)
    {
        var mode = exclusive ? ExclusiveMode : SharedMode;
        var resource = string.Create(CultureInfo.InvariantCulture, $"ecr:doc-header:{documentId}");
        var (code, _) = await GetAppLockAsync(resource, mode, versionOfDocument: null, ct).ConfigureAwait(false);

        // ⚠ Хто чекав, той і читає відмову: правка шапки — на подання, подання — на правку шапки.
        ThrowIfRefused(code, resource, mode, () => DocumentBusy(
            documentId, mode, code,
            exclusive ? "err.ECR-DOC-4091.sheetBeingSubmitted" : "err.ECR-DOC-4091.headerBeingEdited"));
    }

    private async Task AcquireAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, string mode, CancellationToken ct)
    {
        var resource = ResourceOf(documentId, sheetDefId, periodKey.Value);
        var (code, _) = await GetAppLockAsync(resource, mode, versionOfDocument: null, ct).ConfigureAwait(false);
        ThrowIfRefused(code, resource, mode, () => Busy(documentId, sheetDefId, periodKey, mode, code));
    }

    /// <summary>Від'ємний код <c>sp_getapplock</c> — виняток; 0 і 1 — блокування взято.</summary>
    private static void ThrowIfRefused(int code, string resource, string mode, Func<Exception> busy)
    {
        if (code is LockTimedOut or LockDeadlockVictim)
        {
            throw busy();
        }

        if (code < 0)
        {
            // ⚠ Решта від'ємних кодів (-2 — запит скасовано, -999 — помилка
            // параметрів) — не «зайнято», а збій, і вдавати його
            // зрозумілою відмовою означало б сховати дефект.
            throw new InvalidOperationException(
                $"Не вдалося взяти блокування «{resource}» ({mode}): sp_getapplock повернув {code}.");
        }
    }

    /// <summary>
    /// <c>sp_getapplock</c> з власником <c>Transaction</c>; повертає його код і — якщо задано
    /// <paramref name="versionOfDocument"/> і блокування взято — <c>Project.TemplateVersionId</c>
    /// цього документа.
    /// </summary>
    /// <remarks>
    /// ⚠ Одним пакетом, тобто одним зверненням: версія читається ОКРЕМИМ оператором
    /// після <c>EXEC</c>, а під RCSI знімок береться на початку оператора — тож він
    /// бачить перенос, що зафіксувався, поки ми чекали на блокування.
    /// </remarks>
    private async Task<(int Code, int? Version)> GetAppLockAsync(
        string resource, string mode, long? versionOfDocument, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction
                          ?? throw new InvalidOperationException(
                              "Блокування аркуша чи документа береться лише всередині транзакції: " +
                              "поза нею воно звільнилося б одразу і нічого не захистило б.");

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandType = CommandType.Text;
        command.CommandText = """
            DECLARE @code int;
            EXEC @code = sp_getapplock @Resource = @Resource, @LockMode = @LockMode,
                                       @LockOwner = 'Transaction', @LockTimeout = @LockTimeout;
            SET @Result = @code;
            IF @code >= 0 AND @DocumentId IS NOT NULL
                SET @Version = (SELECT p.TemplateVersionId
                                FROM   doc.Document d
                                JOIN   doc.Project  p ON p.Id = d.ProjectId
                                WHERE  d.Id = @DocumentId);
            """;

        command.Parameters.Add(new SqlParameter("@Resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@LockMode", SqlDbType.VarChar, 32) { Value = mode });
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int)
        {
            Value = (int)Math.Min(int.MaxValue, _lockTimeout.TotalMilliseconds),
        });
        command.Parameters.Add(new SqlParameter("@DocumentId", SqlDbType.BigInt)
        {
            Value = versionOfDocument is { } id ? id : DBNull.Value,
        });
        var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.Output };
        var version = new SqlParameter("@Version", SqlDbType.Int) { Direction = ParameterDirection.Output };
        command.Parameters.Add(result);
        command.Parameters.Add(version);

        // ⚠ Таймаут команди — більший за таймаут блокування: інакше клієнт
        // обірвав би очікування раніше, ніж сервер відповів би кодом відмови.
        command.CommandTimeout = (int)Math.Ceiling(_lockTimeout.TotalSeconds) + 15;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // 0 — узято одразу, 1 — узято після очікування; від'ємне — відмова.
        return ((int)result.Value!, version.Value is int v ? v : null);
    }

    /// <summary>Відмова «документ зайнятий» для блокувань рівня документа — <c>409 ECR-DOC-4091</c>.</summary>
    private static ConcurrencyConflictException DocumentBusy(long documentId, string mode, int code, string messageKey)
        => new(
            ErrorCodes.SheetBusy,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Документ {documentId} зайнятий ({mode}, sp_getapplock = {code}): повторіть дію за мить."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = messageKey,
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });

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
