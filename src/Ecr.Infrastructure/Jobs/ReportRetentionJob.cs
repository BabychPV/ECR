using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Прибирає ЗАЙВІ зрізи звітності (<c>B16</c> §4, <c>D-71</c>).
/// </summary>
/// <remarks>
/// ⛔ Q-2xx (аудит фази 3, звітність). Цієї задачі не існувало взагалі, хоча
/// на неї посилається і документ (B16 §4: «Решту прибирає
/// <c>ReportRetentionJob</c>»), і сама доменна модель — <c>Supersede</c> на
/// <see cref="Domain.Entities.Reporting.ReportSnapshot"/> навмисно лишає
/// попередній зріз у базі, а не видаляє його. <c>BuildReportSnapshotHandler</c>
/// створює НОВИЙ зріз на кожен виклик і ніколи не переписує старий (ФВ-9.17):
/// без прибирання <c>rpt.ReportSnapshot</c>/<c>rpt.ReportRow</c> ростуть
/// вічно — на кожен звіт, кожен проєкт, кожен період, кожну повторну
/// побудову. <c>D-71</c> прямо дозволяє це видаляти: правило «нічого не
/// затирається» стосується даних, яких не відтворити (<c>doc.*</c>,
/// <c>aud.*</c>, <c>calc.CalculationResult</c>), а зріз звітності — похідний
/// артефакт, і виняток називає його явно: «крім <c>IsSubmitted</c> і
/// <c>IsCurrent</c>».
/// <para>
/// ⚠ Тому критерій видалення — рівно ці два прапорці: зріз прибирається, лише
/// якщо він одночасно НЕ поточний (<see cref="Domain.Entities.Reporting.ReportSnapshot.IsCurrent"/>)
/// і НЕ поданий (<c>Status != Submitted</c>). Поточний зріз — це те, що
/// повертає <c>rpt.v_*</c> просто зараз; поданий — доказ того, що саме бачив
/// регулятор (ФВ-9.17). Прибрати чернетку чи затверджений зріз, поки він
/// іще поточний, означало б, що регуляторна вʼюха раптом лишається без рядків
/// між однією ніччю й наступною побудовою.
/// </para>
/// <para>
/// ⚠ Рядки (<c>rpt.ReportRow</c>) видаляються ПЕРШИМИ: `FK_RepRow_Snap` —
/// <c>DeleteBehavior.Restrict</c> (не каскадний), і видалення батька першим
/// впало б порушенням зовнішнього ключа.
/// </para>
/// </remarks>
public sealed class ReportRetentionJob(EcrDbContext db, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "report-retention";

    /// <summary>
    /// Скільки зрізів прибирає один прохід одного батчу.
    /// </summary>
    /// <remarks>
    /// Межа тримає розмір параметризованого <c>IN (...)</c> і розмір однієї
    /// транзакції видалення — не тому, що зрізів менше не буває, а щоб забій,
    /// накопичений за місяці простою цієї задачі, не перетворився на
    /// багатогодинне блокування <c>rpt.*</c> за одну ніч.
    /// </remarks>
    private const int BatchSize = 2_000;

    /// <summary>
    /// Скільки батчів забирає один прогін.
    /// </summary>
    /// <remarks>
    /// Заборгованість (перший прогін після довгої відсутності задачі) може
    /// бути більшою за один батч. Стеля не дає одному нічному вікну зʼїсти всю
    /// ніч: решту доїсть завтрашній прогін.
    /// </remarks>
    private const int MaxBatchesPerRun = 20;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-239: див. `MaintenanceRunFailure`. Прибирання зрізів падає
        // найімовірніше саме посеред батчів (таймаут видалення, дедлок на
        // rpt.ReportRow) — і без цього catch прогін лишався б `Running`, а
        // зведення `NotificationJob`, що фільтрує за `FinishedAt >= since`,
        // не сказало б про це ні слова: rpt.* росли б далі мовчки.
        try
        {
            await RunAsync(run, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Власне прибирання; прогін уже відкрито.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task RunAsync(MaintenanceRun run, IJobProgress progress, CancellationToken ct)
    {
        var totalSnapshots = 0;
        var totalRows = 0;
        var batches = 0;

        while (batches < MaxBatchesPerRun)
        {
            // ⚠ Кандидати — НЕ поточні і НЕ подані зрізи (D-71). Обидва
            // прапорці читаються з БАЗИ щоразу заново: перерахунок статусу
            // (`ReportSnapshotSync`) і перемикання `IsCurrent`
            // (`SwitchCurrentAsync`) можуть відбутися між батчами того самого
            // прогону.
            var candidateIds = await db.ReportSnapshots
                .Where(s => !s.IsCurrent && s.Status != SnapshotStatus.Submitted)
                .OrderBy(s => s.Id)
                .Select(s => s.Id)
                .Take(BatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (candidateIds.Count == 0)
            {
                break;
            }

            // ⛔ Рядки ПЕРШИМИ: FK_RepRow_Snap не каскадний (DeleteBehaviorTests
            // тримає це свідомо для rpt.*), і видалення зрізу раніше за його
            // рядки впало б порушенням зовнішнього ключа.
            var rowsDeleted = await db.ReportRows
                .Where(r => candidateIds.Contains(r.SnapshotId))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            var snapshotsDeleted = await db.ReportSnapshots
                .Where(s => candidateIds.Contains(s.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            totalRows += rowsDeleted;
            totalSnapshots += snapshotsDeleted;
            batches++;

            await progress
                .ReportAsync(
                    Math.Min(99, batches * 100 / MaxBatchesPerRun),
                    $"Прибрано зрізів: {totalSnapshots}, рядків: {totalRows}",
                    ct)
                .ConfigureAwait(false);

            // Батч менший за стелю — заборгованості більше немає, чекати
            // наступний батч нема сенсу.
            if (candidateIds.Count < BatchSize)
            {
                break;
            }
        }

        run.Complete(
            "Succeeded",
            $$"""{"snapshotsDeleted":{{totalSnapshots}},"rowsDeleted":{{totalRows}},"batches":{{batches}}}""",
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await progress
            .ReportAsync(100, $"Прибрано зрізів: {totalSnapshots}, рядків: {totalRows}", ct)
            .ConfigureAwait(false);
    }
}
