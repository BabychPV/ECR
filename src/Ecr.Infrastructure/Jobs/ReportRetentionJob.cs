using System.Globalization;
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
/// <para>
/// ⛔ P1-08. Тим самим нічним прогоном прибираються й ЗАЙВІ прогони перевірки документа
/// (<c>wf.ValidationResult</c>): кожне «Перевірити» дописує рядок із повним <c>MessagesJson</c> (сотні КБ для
/// документа з порожніми обов'язковими полями), і до цієї ретенції рядки видалялися лише разом із документом
/// (<c>DocumentDeletionStore</c>). Лишаються <see cref="ValidationRunsKept"/> НАЙНОВІШИХ прогонів на кожну пару
/// <c>(DocumentId, PeriodKey)</c>: усі читачі (<c>GetLatestAsync</c>, лічильники переліку, міграція версії) беруть
/// лише останній прогін, а історію «коли документ став валідним» тримають свіжі прогони.
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

    /// <summary>Скільки НАЙНОВІШИХ прогонів перевірки лишається на кожну пару «документ + період» (P1-08).</summary>
    internal const int ValidationRunsKept = 20;

    /// <summary>Скільки прогонів перевірки видаляє один оператор <c>DELETE</c> (P1-08).</summary>
    /// <remarks>
    /// Рядок несе <c>nvarchar(max)</c> із повідомленнями, тож межа тримає розмір транзакції й журналу, а не лише
    /// число рядків: забій, накопичений за місяці без ретенції, не перетворюється на один гігантський оператор.
    /// </remarks>
    internal const int ValidationBatchSize = 1_000;

    /// <summary>Скільки батчів прогонів перевірки забирає один нічний прогін; решту доїдає наступний.</summary>
    internal const int MaxValidationBatchesPerRun = 20;

    /// <summary>
    /// Зрізи, молодші за цю межу (за <see cref="Domain.Entities.Reporting.ReportSnapshot.BuiltAt"/>),
    /// не прибираються: можливо, вони ще будуються.
    /// </summary>
    /// <remarks>
    /// ⛔ R7-Y2-03. <c>ReportSnapshotBuilder</c> комітить новий зріз як <c>Draft</c> з
    /// <c>IsCurrent = 0</c>, потім ОКРЕМИМ збереженням (поза замком слоту) пише рядки, і лише третьою
    /// транзакцією під замком слоту робить зріз поточним. Між першим і третім кроком зріз за всіма
    /// ознаками схожий на «старий непоточний» (D-71). Ретенція видаляла його рядки, побудова
    /// перемикала <c>IsCurrent = 1</c>, а другий <c>DELETE</c> зріз уже пропускав — і поточним ставав
    /// зріз без жодного рядка (<c>RowCount</c> і хеш при цьому — повні): регулятор бачив порожній звіт.
    /// <para>
    /// ⚠ Вікова межа, а не замок слоту: <c>BuiltAt</c> ставиться ДО агрегації (X7-04), тож усе вікно
    /// побудови лежить після нього, а побудова триває хвилини, не години. Шість годин перекривають
    /// будь-яку побудову з великим запасом; D-71 вікової межі не забороняє — прибирання лише
    /// відкладається на наступну ніч. Безпечний бік: зайвий зріз проживе добу довше, а не поточний
    /// зріз лишиться порожнім.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan BuildGrace = TimeSpan.FromHours(6);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-240: див. `MaintenanceRunFailure`. Прибирання зрізів падає
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

    /// <summary>
    /// P1-08: лишає <see cref="ValidationRunsKept"/> найновіших прогонів перевірки на «документ + період», решту
    /// видаляє обмеженими батчами.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки прогонів видалено.</returns>
    /// <remarks>
    /// ⚠ Один оператор: <c>ROW_NUMBER</c> за <c>(DocumentId, PeriodKey)</c> від найновішого (<c>RunAt DESC, Id DESC</c>
    /// — той самий порядок, що вибирає «останній» у <c>GetLatestAsync</c>) іде по <c>IX_ValidationResult_Doc</c> без
    /// читання <c>MessagesJson</c>; <c>TOP</c> зупиняє пошук на першому ж батчі. Нові прогони лише ЗБІЛЬШУЮТЬ ранг
    /// старих, тож гонка з паралельним «Перевірити» не може зробити видалення неправомірним.
    /// </remarks>
    private async Task<int> PruneValidationResultsAsync(CancellationToken ct)
    {
        var total = 0;

        for (var batch = 0; batch < MaxValidationBatchesPerRun; batch++)
        {
            var deleted = await db.Database
                .ExecuteSqlAsync(
                    $"""
                    DELETE FROM wf.ValidationResult
                     WHERE Id IN (
                           SELECT TOP ({ValidationBatchSize}) r.Id
                             FROM (SELECT Id,
                                          ROW_NUMBER() OVER (PARTITION BY DocumentId, PeriodKey
                                                             ORDER BY RunAt DESC, Id DESC) AS Rn
                                     FROM wf.ValidationResult) AS r
                            WHERE r.Rn > {ValidationRunsKept});
                    """,
                    ct)
                .ConfigureAwait(false);

            total += deleted;

            if (deleted < ValidationBatchSize)
            {
                break;
            }
        }

        return total;
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

        // ⛔ R7-Y2-03: див. `BuildGrace`. Межа одна на весь прогін і стоїть у ВСІХ трьох запитах.
        var builtBefore = clock.UtcNow - BuildGrace;

        while (batches < MaxBatchesPerRun)
        {
            // ⚠ Кандидати — НЕ поточні і НЕ подані зрізи (D-71). Обидва
            // прапорці читаються з БАЗИ щоразу заново: перерахунок статусу
            // (`ReportSnapshotSync`) і перемикання `IsCurrent`
            // (`SwitchCurrentAsync`) можуть відбутися між батчами того самого
            // прогону.
            var candidateIds = await db.ReportSnapshots
                .Where(s => !s.IsCurrent && s.Status != SnapshotStatus.Submitted && s.BuiltAt < builtBefore)
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
            //
            // ⛔ Умова `!IsCurrent && Status != Submitted` ПОВТОРЮЄТЬСЯ у
            // `Where` самого видалення, а не лише в матеріалізованому вище
            // переліку id (аудит 2026-09-16, §6.2). Між вибором кандидатів і
            // видаленням існувало вікно, у якому хтось міг зробити зріз
            // поточним або подати його як регуляторний доказ — і завдання
            // видаляло б його попри доменний інваріант D-71 («ніколи не
            // видаляти поточний чи поданий зріз»), НАЗАВЖДИ, бо видалення
            // жорстке. Перелік id лишається: він задає ПАРТІЮ (`Take`), а
            // умова нижче — ПРАВО видалити.
            var rowsDeleted = await db.ReportRows
                .Where(r => candidateIds.Contains(r.SnapshotId)
                            && db.ReportSnapshots.Any(
                                s => s.Id == r.SnapshotId
                                     && !s.IsCurrent
                                     && s.Status != SnapshotStatus.Submitted
                                     && s.BuiltAt < builtBefore))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            var snapshotsDeleted = await db.ReportSnapshots
                .Where(s => candidateIds.Contains(s.Id)
                            && !s.IsCurrent
                            && s.Status != SnapshotStatus.Submitted
                            && s.BuiltAt < builtBefore)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            totalRows += rowsDeleted;
            totalSnapshots += snapshotsDeleted;
            batches++;

            await progress
                .ReportKeyAsync(
                    Math.Min(99, batches * 100 / MaxBatchesPerRun),
                    "jobs.retentionCleared",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["snapshots"] = totalSnapshots.ToString(CultureInfo.InvariantCulture),
                        ["rows"] = totalRows.ToString(CultureInfo.InvariantCulture),
                    },
                    ct)
                .ConfigureAwait(false);

            // Батч менший за стелю — заборгованості більше немає, чекати
            // наступний батч нема сенсу.
            if (candidateIds.Count < BatchSize)
            {
                break;
            }
        }

        var validationDeleted = await PruneValidationResultsAsync(ct).ConfigureAwait(false);

        run.Complete(
            "Succeeded",
            $$"""{"snapshotsDeleted":{{totalSnapshots}},"rowsDeleted":{{totalRows}},"batches":{{batches}},"validationResultsDeleted":{{validationDeleted}}}""",
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await progress
            .ReportKeyAsync(
                100,
                "jobs.retentionCleared",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["snapshots"] = totalSnapshots.ToString(CultureInfo.InvariantCulture),
                    ["rows"] = totalRows.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);
    }
}
