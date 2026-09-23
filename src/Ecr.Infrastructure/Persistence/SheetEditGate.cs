using System.Data;
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
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
/// </remarks>
public sealed class SheetEditGate(EcrDbContext db) : ISheetEditGate
{
    /// <summary>Скільки чекати на блокування, мс.</summary>
    /// <remarks>
    /// ⚠ Подання триває секунди (валідація + зріз). Тридцять секунд — з
    /// великим запасом; довше — це вже не черга, а щось зависле, і тоді чесніше
    /// відмовити, ніж тримати запит нескінченно.
    /// </remarks>
    private const int LockTimeoutMs = 30_000;

    /// <inheritdoc />
    public async Task<DocumentStatus> EnterEditAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct)
    {
        await AcquireAsync(documentId, sheetDefId, periodKey, "Shared", ct).ConfigureAwait(false);

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
        => AcquireAsync(documentId, sheetDefId, periodKey, "Exclusive", ct);

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
        command.Parameters.Add(new SqlParameter("@LockTimeout", SqlDbType.Int) { Value = LockTimeoutMs });
        var result = new SqlParameter("@Result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
        command.Parameters.Add(result);

        // ⚠ Таймаут команди — більший за таймаут блокування: інакше клієнт
        // обірвав би очікування раніше, ніж сервер відповів би кодом відмови.
        command.CommandTimeout = (LockTimeoutMs / 1000) + 15;

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        // 0 — узято одразу, 1 — узято після очікування; від'ємне — відмова.
        var code = (int)result.Value!;
        if (code < 0)
        {
            // ⚠ `InvalidOperationException`, а не `TimeoutException`: останній
            // стратегія повторів EF вважає транзієнтним і повторила б усе тіло
            // транзакції, тобто очікування помножилося б на число повторів.
            throw new InvalidOperationException(
                $"Не вдалося взяти блокування аркуша «{resource}» ({mode}): sp_getapplock повернув {code}.");
        }
    }
}
