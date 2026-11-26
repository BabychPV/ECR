using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Нічна ретенція прогонів перевірки документа (<c>wf.ValidationResult</c>, P1-08).
/// </summary>
/// <remarks>
/// ✎ P-1124-3 (D-NEW, рішення людини 24.11 «Прибрати повністю»): прибирання зрізів звітності
/// (<c>rpt.ReportSnapshot</c>/<c>rpt.ReportRow</c>, B16 §4, D-71) прибрано разом із самими зрізами — їх більше
/// ніхто не будує, звітність лише SSRS над сирим шаром <c>rpt.*</c>. Таблиці зрізів лишаються недоторканими
/// (контракт D-53), видаляти з них рядки ця задача більше не має права. Ім'я класу й код
/// <see cref="Code"/> лишаються ті самі: за кодом нічне наздоганяння (<c>NightlyCatchUp</c>) шукає останній прогін
/// у <c>itg.MaintenanceRun</c>, і новий код загубив би історію.
/// <para>
/// ⛔ P1-08. Кожне «Перевірити» дописує рядок із повним <c>MessagesJson</c> (сотні КБ для документа з порожніми
/// обов'язковими полями), і без ретенції рядки видалялися лише разом із документом (<c>DocumentDeletionStore</c>).
/// Лишаються <see cref="ValidationRunsKept"/> НАЙНОВІШИХ прогонів на кожну пару <c>(DocumentId, PeriodKey)</c>: усі
/// читачі (<c>GetLatestAsync</c>, лічильники переліку, міграція версії) беруть лише останній прогін, а історію «коли
/// документ став валідним» тримають свіжі прогони.
/// </para>
/// </remarks>
public sealed class ReportRetentionJob(EcrDbContext db, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "report-retention";

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

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-240: див. `MaintenanceRunFailure`. Без цього catch прогін, що впав посеред батчів (таймаут видалення,
        // дедлок), лишався б `Running`, а зведення `NotificationJob`, що фільтрує за `FinishedAt >= since`, не сказало б
        // про це ні слова.
        try
        {
            var validationDeleted = await PruneValidationResultsAsync(ct).ConfigureAwait(false);

            run.Complete("Succeeded", $$"""{"validationResultsDeleted":{{validationDeleted}}}""", clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportAsync(100, null, ct).ConfigureAwait(false);
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
}
