// src/Ecr.Infrastructure/Jobs/SubmissionSnapshotRetentionJob.cs
using System.Data;
using System.Data.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Прибирання старих зрізів подання <c>calc.SubmissionSnapshot</c> (AN-134): кожне подання аркуша додає зріз
/// <c>PayloadJson</c> ~10 МБ, а політики прибирання не було.
/// </summary>
/// <remarks>
/// ⛔ Що НЕ видаляється ніколи (обидві умови разом — зріз лишається, якщо підходить хоч під одну):
/// <list type="number">
/// <item>
/// <b><see cref="SnapshotRetentionOptions.RetainSnapshots"/> найновіших</b> зрізів на «документ × аркуш × період»
/// (порядок <c>SubmittedAt DESC, Id DESC</c> — той самий, що в <c>WorkflowStore.GetSnapshotsAsync</c> і списку версій).
/// </item>
/// <item>
/// <b>Зрізи погодження.</b> Прямого посилання на зріз немає ні в <c>wf.ApprovalState</c>, ні в
/// <c>wf.ApprovalEvent</c> (перевірено: жодного FK/колонки), тому зв'язок — часом: погодження
/// (<c>ApprovalEvent.Action IN (Approve, ApproveStep)</c> і <c>ApprovalState.ApprovedAt</c>) і
/// <c>ApprovalSnapshotRefresh</c> беруть один <c>now</c>, отже зріз погодження — НАЙНОВІШИЙ зріз цієї трійки не
/// пізніше за момент погодження (з запасом <see cref="ApprovalSlackSeconds"/> c на точність типу). Це і зріз
/// подання (без правок погоджувача), і зріз після правки погоджувача (D-442) — «доказ того, що погоджено».
/// Крок маршруту (<c>ApproveStep</c>) теж підпис, тож береться так само.
/// </item>
/// </list>
/// Споживачі зрізів: список версій і порівняння (<c>DocumentVersionStore</c>), «останній зріз» у
/// <c>ApprovalSnapshotRefresh</c>, звітність (читає живі комірки, не зрізи), заборона видалення документа з
/// зрізами (<c>DocumentDeletionStore</c> — лишається, бо ≥ 2 зрізи завжди живі). Скасування/Reopen зріз не чіпають.
/// <para>
/// ⚠ Безпечно при конкурентному поданні: новий зріз стає №1 і зсуває старі ВНИЗ — кандидат лише додається,
/// ніколи не зникає з-під захисту; сам новий зріз кандидатом бути не може. Один оператор <c>DELETE</c> =
/// одна коротка транзакція, БЕЗ замка аркуша (<c>ISheetEditGate</c>) — подання не чекає прибирання.
/// </para>
/// <para>
/// ⚠ Без міграції: індекс <c>IX_SubmissionSnapshot_Sheet (DocumentId, SheetDefId, PeriodKey, SubmittedAt DESC)</c>
/// вже віддає потрібний порядок; <c>PayloadJson</c> не читається, крім <c>DATALENGTH</c> для підрахунку байтів.
/// </para>
/// <para>
/// ⛔ Видалення вимкнено за замовчуванням (<see cref="SnapshotRetentionOptions.DeleteEnabled"/>): прогін лише лічить
/// і пише в журнал, скільки зрізів і байтів буде видалено. Суперечить ли це «зрізи накопичуються і лишаються
/// назавжди» (ФВ-9.17, <c>SubmissionSnapshot</c>) — рішення людини; тому без її слова нічого не видаляється.
/// </para>
/// </remarks>
public sealed partial class SubmissionSnapshotRetentionJob(
    EcrDbContext db, IClock clock, IConfiguration configuration, ILogger<SubmissionSnapshotRetentionJob> logger)
    : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "submission-snapshot-retention";

    /// <summary>Запас, c: зріз погодження може відрізнятися від моменту погодження на точність типу часу.</summary>
    public const int ApprovalSlackSeconds = 1;

    /// <summary>Таймаут одного оператора: підрахунок dry-run читає довжини payload усієї таблиці.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    // Спільна частина: Ranked — номер від найновішого; Pinned — зрізи погодження; Candidates — що МОЖНА видалити.
    private const string CandidatesCte = """
        WITH Ranked AS (
            SELECT s.Id,
                   ROW_NUMBER() OVER (PARTITION BY s.DocumentId, s.SheetDefId, s.PeriodKey
                                      ORDER BY s.SubmittedAt DESC, s.Id DESC) AS Rn
              FROM calc.SubmissionSnapshot AS s),
        Markers AS (
            SELECT e.DocumentId, e.SheetDefId, e.PeriodKey, e.[At] AS MarkedAt
              FROM wf.ApprovalEvent AS e
             WHERE e.[Action] IN (2, 3)
            UNION
            SELECT a.DocumentId, a.SheetDefId, a.PeriodKey, a.ApprovedAt
              FROM wf.ApprovalState AS a
             WHERE a.ApprovedAt IS NOT NULL),
        Pinned AS (
            SELECT p.Id
              FROM Markers AS m
             CROSS APPLY (SELECT TOP (1) s.Id
                            FROM calc.SubmissionSnapshot AS s
                           WHERE s.DocumentId = m.DocumentId
                             AND s.SheetDefId = m.SheetDefId
                             AND s.PeriodKey = m.PeriodKey
                             AND s.SubmittedAt <= DATEADD(SECOND, @slack, m.MarkedAt)
                           ORDER BY s.SubmittedAt DESC, s.Id DESC) AS p),
        Candidates AS (
            SELECT r.Id
              FROM Ranked AS r
             WHERE r.Rn > @retain
               AND NOT EXISTS (SELECT 1 FROM Pinned AS p WHERE p.Id = r.Id))
        """;

    private const string DryRunSql = CandidatesCte + """

        SELECT (SELECT COUNT_BIG(*) FROM Candidates) AS Candidates,
               (SELECT COALESCE(SUM(CAST(DATALENGTH(s.PayloadJson) AS bigint)), 0)
                  FROM calc.SubmissionSnapshot AS s
                  JOIN Candidates AS c ON c.Id = s.Id) AS Bytes,
               (SELECT COUNT_BIG(*) FROM Ranked AS r
                 WHERE r.Rn > @retain AND EXISTS (SELECT 1 FROM Pinned AS p WHERE p.Id = r.Id)) AS PinnedKept;
        """;

    private const string DeleteBatchSql = """
        SET DEADLOCK_PRIORITY LOW;
        DECLARE @out TABLE (Bytes bigint NOT NULL);

        """ + CandidatesCte + """

        DELETE s
        OUTPUT CAST(DATALENGTH(deleted.PayloadJson) AS bigint) INTO @out
          FROM calc.SubmissionSnapshot AS s
         WHERE s.Id IN (SELECT TOP (@batch) c.Id FROM Candidates AS c ORDER BY c.Id);

        SET DEADLOCK_PRIORITY NORMAL;
        SELECT COUNT_BIG(*) AS Deleted, COALESCE(SUM(Bytes), 0) AS Bytes FROM @out;
        """;

    /// <summary>Скільки разів повторити пакет, якщо він став жертвою взаємного блокування.</summary>
    private const int DeadlockRetries = 3;

    /// <summary>Номер помилки SQL Server «жертва взаємного блокування».</summary>
    private const int DeadlockVictimError = 1205;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var options = SnapshotRetentionOptions.Read(configuration);
            var outcome = options.Enabled
                ? await RunAsync(options, progress, ct).ConfigureAwait(false)
                : SnapshotRetentionOutcome.Disabled(options);

            run.Complete("Succeeded", outcome.ToJson(), clock.UtcNow);
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
    /// Один прохід: dry-run (лічить) або видалення пакетами. Публічний для тестів і ручного запуску.
    /// </summary>
    /// <param name="options">Налаштування.</param>
    /// <param name="progress">Канал прогресу; <c>null</c> — без звіту.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<SnapshotRetentionOutcome> RunAsync(
        SnapshotRetentionOptions options, IJobProgress? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.RetainSnapshots < SnapshotRetentionOptions.MinRetainSnapshots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.RetainSnapshots,
                $"RetainSnapshots не може бути меншим за {SnapshotRetentionOptions.MinRetainSnapshots}.");
        }

        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return options.DeleteEnabled
                ? await DeleteAsync(options, progress, ct).ConfigureAwait(false)
                : await CountAsync(options, ct).ConfigureAwait(false);
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private async Task<SnapshotRetentionOutcome> CountAsync(SnapshotRetentionOptions options, CancellationToken ct)
    {
        await using var command = CreateCommand(DryRunSql, options, batch: null);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        var outcome = new SnapshotRetentionOutcome(
            DryRun: true,
            Retain: options.RetainSnapshots,
            Candidates: reader.GetInt64(0),
            Bytes: reader.GetInt64(1),
            PinnedKept: reader.GetInt64(2),
            Deleted: 0,
            DeletedBytes: 0,
            Batches: 0);

        LogDryRun(logger, outcome.Candidates, outcome.Bytes, outcome.PinnedKept, outcome.Retain);
        return outcome;
    }

    private async Task<SnapshotRetentionOutcome> DeleteAsync(
        SnapshotRetentionOptions options, IJobProgress? progress, CancellationToken ct)
    {
        long deleted = 0, bytes = 0;
        var batches = 0;

        while (batches < options.MaxBatchesPerRun)
        {
            var (batchDeleted, batchBytes) = await DeleteBatchAsync(options, ct).ConfigureAwait(false);

            deleted += batchDeleted;
            bytes += batchBytes;
            batches++;

            LogBatch(logger, batches, batchDeleted, batchBytes);

            if (progress is not null)
            {
                await progress.ReportAsync(Math.Min(99, batches * 100 / options.MaxBatchesPerRun), null, ct)
                    .ConfigureAwait(false);
            }

            // Пакет менший за межу — кандидатів більше немає.
            if (batchDeleted < options.BatchSize)
            {
                break;
            }

            if (options.PauseBetweenBatches > TimeSpan.Zero)
            {
                await Task.Delay(options.PauseBetweenBatches, ct).ConfigureAwait(false);
            }
        }

        var outcome = new SnapshotRetentionOutcome(
            DryRun: false,
            Retain: options.RetainSnapshots,
            Candidates: deleted,
            Bytes: bytes,
            PinnedKept: 0,
            Deleted: deleted,
            DeletedBytes: bytes,
            Batches: batches);

        LogDeleted(logger, deleted, bytes, batches, options.RetainSnapshots);
        return outcome;
    }

    /// <summary>
    /// Один пакет. ⚠ Прибирання — завжди жертва взаємного блокування (<c>DEADLOCK_PRIORITY LOW</c>), а не
    /// подання аркуша; жертву повторюємо кілька разів, далі помилка йде нагору (наступна ніч доїсть).
    /// </summary>
    private async Task<(long Deleted, long Bytes)> DeleteBatchAsync(SnapshotRetentionOptions options, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var command = CreateCommand(DeleteBatchSql, options, options.BatchSize);
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await reader.ReadAsync(ct).ConfigureAwait(false);
                return (reader.GetInt64(0), reader.GetInt64(1));
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
                when (ex.Number == DeadlockVictimError && attempt < DeadlockRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private DbCommand CreateCommand(string sql, SnapshotRetentionOptions options, int? batch)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = (int)CommandTimeout.TotalSeconds;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        AddParameter(command, "@retain", options.RetainSnapshots);
        AddParameter(command, "@slack", ApprovalSlackSeconds);

        if (batch is { } size)
        {
            AddParameter(command, "@batch", size);
        }

        return command;
    }

    private static void AddParameter(DbCommand command, string name, int value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.Int32;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання зрізів подання (dry-run, нічого не видалено): було б видалено {Candidates} зрізів, "
            + "{Bytes} байт; зрізів погодження збережено понад межу — {PinnedKept}; лишається останніх — {Retain}.")]
    private static partial void LogDryRun(ILogger logger, long candidates, long bytes, long pinnedKept, int retain);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Прибирання зрізів подання: пакет {Batch} — видалено {Deleted} зрізів, {Bytes} байт.")]
    private static partial void LogBatch(ILogger logger, int batch, long deleted, long bytes);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Прибирання зрізів подання: видалено {Deleted} зрізів, {Bytes} байт, пакетів — {Batches}; "
            + "лишається останніх — {Retain} і всі зрізи погодження.")]
    private static partial void LogDeleted(ILogger logger, long deleted, long bytes, int batches, int retain);
}

/// <summary>Підсумок проходу прибирання зрізів (лічильники для журналу та <c>MaintenanceRun.DetailsJson</c>).</summary>
/// <param name="DryRun"><c>true</c> — нічого не видалено, лише пораховано.</param>
/// <param name="Retain">Скільки останніх зрізів лишається на трійку.</param>
/// <param name="Candidates">Кандидатів на видалення (dry-run) або видалено (видалення).</param>
/// <param name="Bytes">Байт payload кандидатів/видалених.</param>
/// <param name="PinnedKept">Зрізів погодження, що пережили б межу «останніх N» (лише dry-run).</param>
/// <param name="Deleted">Видалено зрізів.</param>
/// <param name="DeletedBytes">Видалено байт.</param>
/// <param name="Batches">Пакетів видалення.</param>
public sealed record SnapshotRetentionOutcome(
    bool DryRun, int Retain, long Candidates, long Bytes, long PinnedKept, long Deleted, long DeletedBytes, int Batches)
{
    /// <summary>Підсумок вимкненого проходу (<c>Enabled=false</c>).</summary>
    /// <param name="options">Налаштування.</param>
    public static SnapshotRetentionOutcome Disabled(SnapshotRetentionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new SnapshotRetentionOutcome(true, options.RetainSnapshots, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Підсумок для <c>MaintenanceRun.DetailsJson</c>.</summary>
    public string ToJson()
        => $$"""{"mode":"{{(DryRun ? "dryRun" : "delete")}}","retain":{{Retain}},"candidates":{{Candidates}},"bytes":{{Bytes}},"pinnedKept":{{PinnedKept}},"deleted":{{Deleted}},"deletedBytes":{{DeletedBytes}},"batches":{{Batches}}}""";
}
