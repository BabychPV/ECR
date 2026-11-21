using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Стежить за запасом порожніх партицій попереду.
/// </summary>
/// <remarks>
/// ⚠ Працює **на випередження**. <c>SPLIT</c> порожньої останньої партиції —
/// операція метаданих; <c>SPLIT</c> непорожньої переміщує дані з блокуванням.
/// Дізнатися про вичерпаний запас треба на старті або за розкладом, а не
/// вночі під час архівації.
/// </remarks>
public sealed partial class PartitionCheckJob(
    EcrDbContext db, ISqlCapabilities capabilities, IClock clock, ILogger<PartitionCheckJob>? logger = null)
    : IBackgroundJob
{
    private readonly ILogger log = logger ?? NullLogger<PartitionCheckJob>.Instance;

    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "partition-check";

    /// <summary>
    /// Скільки меж попереду вважається достатнім запасом.
    /// </summary>
    /// <remarks>
    /// Дві — це мінімум, не комфорт: одна витрачається на поточний місяць,
    /// друга лишається запасом на час, поки хтось прочитає алерт.
    /// </remarks>
    public const int MinimumBoundariesAhead = 2;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Q-240: див. `MaintenanceRunFailure`. Без цього catch виняток
        // лишав прогін `Running`/`FinishedAt = NULL`, а зведення бере збої за
        // `FinishedAt >= since` — тобто провал перевірки запасу партицій не
        // потрапляв у зведення й ні в чий лист.
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

    /// <summary>Власне перевірка; прогін уже відкрито.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task RunAsync(MaintenanceRun run, IJobProgress progress, CancellationToken ct)
    {
        // Аудит: межі pf_AuditByMonth продовжуються на AuditMonthsAhead місяців
        // уперед процедурою (EXECUTE AS OWNER, тож DDL-прав застосунку не треба —
        // D-66). Ідемпотентно: додає лише відсутні межі; SPLIT порожньої крайньої
        // партиції — операція метаданих.
        // ⛔ L2-05 (HU-11 Q8, дефолт A): збій продовження (немає GRANT EXECUTE на arc.*
        // у перші дні за D-264, таймаут блокування SPLIT) — стан Degraded, а перевірка
        // запасу pf_ByPeriodKey нижче виконується ЗАВЖДИ. Повтор — наступної ночі.
        var (auditAdded, boundariesError) = await ExtendAuditBoundariesAsync(ct).ConfigureAwait(false);
        var boundariesJson = boundariesError is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $",\"auditBoundariesFailed\":true,\"auditBoundariesErrorNumber\":{boundariesError.Number}");

        // D-247: архівація старих партицій аудиту (SWITCH у arc.Audit*) тією ж нічною
        // задачею; процедура ідемпотентна й сама пише слід у itg.MaintenanceRun.
        // Збій архівації НЕ має ховати перевірку запасу партицій нижче: він іде в
        // подробиці прогону й опускає статус до Degraded (сам рядок Failed процедура
        // вже записала).
        // ✎ PSS-27: поріг — налаштування `Retention.AuditMonths` (sys_ecr.SystemSetting, читається щоночі без кешу,
        // підлога 120 міс. — рішення людини 20.11 №7); ту саму підлогу тримає й процедура.
        var retention = await RetentionSettings.ReadAsync(db, ct, log).ConfigureAwait(false);
        var (archivedPartitions, archivedRows, archiveError) =
            await ArchiveAuditAsync(retention.AuditMonths, ct).ConfigureAwait(false);
        var archiveJson = string.Create(
            CultureInfo.InvariantCulture,
            $",\"auditArchiveMonths\":{retention.AuditMonths},\"auditArchivedPartitions\":{archivedPartitions},\"auditArchivedRows\":{archivedRows}{(archiveError is null ? string.Empty : $",\"auditArchiveFailed\":true,\"auditArchiveErrorNumber\":{archiveError.Number}")}{boundariesJson}");
        var auditFailed = archiveError is not null || boundariesError is not null;

        // ⛔ Функції партиціонування може не бути — тоді запасу не існує як
        // поняття, і мовчати про це не можна: «перевірка пройшла» на базі без
        // партицій означала б, що вичерпання диска не помітить ніхто.
        //
        // ⚠ Перевіряється НАЯВНІСТЬ функції, а не редакція: партиціонування
        // доступне в усіх редакціях від SQL Server 2016 SP1, включно з
        // Express, і судити про нього за іменем редакції означало б відмовити
        // там, де все працює (АРХ-7).
        if (!await PartitionFunctionExistsAsync(ct).ConfigureAwait(false))
        {
            run.Complete(
                "Degraded",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"reason\":\"pf_ByPeriodKey не існує\",\"edition\":\"{capabilities.EditionName}\",\"auditBoundariesAdded\":{auditAdded}{archiveJson}}}"),
                clock.UtcNow);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportKeyAsync(100, "jobs.partitionUnavailable", ct).ConfigureAwait(false);
            return;
        }

        var currentKey = CurrentPeriodKey(clock.UtcNow);
        var ahead = await BoundariesAheadAsync(currentKey, ct).ConfigureAwait(false);

        var enough = ahead >= MinimumBoundariesAhead;

        // ✎ PSS-09: рядки в КРАЙНІХ партиціях pf_ByPeriodKey. Ліва (до першої межі, 202601) — документи історичних
        // років (дозволені: історію не мігруємо, рішення людини 20.11 №6); права (від останньої межі) — «далекий» рік
        // (дозволено до 2100). ✎ рев'ю M1: це ПОПЕРЕДЖЕННЯ (`edgeWarning` у подробицях, рядок у журналі служби), а не
        // Degraded: щонічний Degraded без дії, яку можна зробити зараз, лише привчає не читати зведення. Чим це
        // загрожує — наступний SPLIT переносить рядки >= межі (`usp_EnsurePartitions` @MaxRowsToMove) і архівація року
        // (THROW 50013); що робити — runbook, рядок «edgeRows/refused».
        var (firstRows, lastRows) = await EdgeRowsAsync(ct).ConfigureAwait(false);
        var edgeWarning = firstRows > 0 || lastRows > 0;
        if (edgeWarning)
        {
            LogEdgeRows(log, firstRows, lastRows);
        }

        // ⚠ Достатній запас НЕ породжує шуму: задача, що пише попередження
        // щоночі, привчає його не читати — і справжнє попередження губиться
        // серед звичних.
        run.Complete(
            enough && !auditFailed ? "Succeeded" : "Degraded",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"boundariesAhead\":{ahead},\"minimum\":{MinimumBoundariesAhead},\"edgeRows\":{{\"first\":{firstRows},\"last\":{lastRows}}}{(edgeWarning ? ",\"edgeWarning\":true" : string.Empty)},\"auditBoundariesAdded\":{auditAdded}{archiveJson}}}"),
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await progress
            .ReportKeyAsync(
                100,
                enough ? "jobs.partitionAhead" : "jobs.partitionShortage",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ahead"] = ahead.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        // ⛔ Задача НЕ виконує DDL. `SPLIT` робить SQL Agent під окремим
        // principal (D-66): обліковий запис застосунку DDL-прав у PROD не має
        // і мати не повинен. Право створювати партиції — це право створювати
        // будь-що; задача, яка ним володіє, перестає бути безпечною.
    }

    /// <summary>
    /// Стеля вибірки меж партиціонування.
    /// </summary>
    /// <remarks>
    /// Меж у роках наперед — десятки. Тисяча означає, що обслуговування
    /// партицій колись зациклилося, і читати їх усі немає сенсу.
    /// </remarks>
    private const int MaxBoundaries = 1_000;

    /// <summary>Запас меж <c>pf_AuditByMonth</c> уперед, місяців.</summary>
    public const int AuditMonthsAhead = 12;

    /// <summary>
    /// Архівує старі партиції аудиту процедурою <c>arc.usp_ArchiveAudit</c> (EXEC, без DDL у C#).
    /// </summary>
    /// <param name="olderThanMonths">
    /// Глибина аудиту в <c>aud.*</c>, місяців (<see cref="RetentionSettings.AuditMonthsKey"/>, не менше
    /// <see cref="RetentionSettings.MinAuditMonths"/>; було — константа 24).
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ L2-05: причина збою — у журнал служби повністю, у <c>DetailsJson</c> — лише номер
    /// помилки SQL (текст бази туди не йде: він доїжджає в лист і вебхуки, V-03 / L2-13).
    /// </remarks>
    private async Task<(int Partitions, long Rows, Microsoft.Data.SqlClient.SqlException? Error)> ArchiveAuditAsync(
        int olderThanMonths, CancellationToken ct)
    {
        var partitions = new Microsoft.Data.SqlClient.SqlParameter("@PartitionsSwitched", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };
        var rows = new Microsoft.Data.SqlClient.SqlParameter("@RowsSwitched", System.Data.SqlDbType.BigInt)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        try
        {
            await ExecLongAsync(
                    "EXEC arc.usp_ArchiveAudit @OlderThanMonths = {0}, @Today = {1}, @PartitionsSwitched = @PartitionsSwitched OUTPUT, @RowsSwitched = @RowsSwitched OUTPUT;",
                    [olderThanMonths, clock.UtcNow.Date, partitions, rows],
                    ct)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            LogAuditArchiveFailed(log, ex.Number, ex);
            return (0, 0, ex);
        }

        return (partitions.Value is int p ? p : 0, rows.Value is long r ? r : 0, null);
    }

    /// <summary>Продовжує межі аудиту; повертає, скільки меж додано, або збій.</summary>
    private async Task<(int Added, Microsoft.Data.SqlClient.SqlException? Error)> ExtendAuditBoundariesAsync(
        CancellationToken ct)
    {
        var added = new Microsoft.Data.SqlClient.SqlParameter("@Added", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        var today = clock.UtcNow.Date;
        try
        {
            await ExecLongAsync(
                    "EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = {0}, @Today = {1}, @Added = @Added OUTPUT;",
                    [AuditMonthsAhead, today, added],
                    ct)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            LogAuditBoundariesFailed(log, ex.Number, ex);
            return (0, ex);
        }

        return (added.Value is int n ? n : 0, null);
    }

    /// <summary>
    /// EXEC процедури обслуговування під довгим таймаутом команди, з відновленням попереднього.
    /// </summary>
    /// <remarks>
    /// ⛔ L2-05 (той самий дефект, що S-09/S-19 для <see cref="ArchiveJob"/>): глобальні 60 с
    /// на першому прогоні з історією понад 24 міс. обривали архівацію атеншеном клієнта, який
    /// обходить CATCH процедури, — і рядка <c>audit-archive Failed</c> не лишалося.
    /// </remarks>
    private async Task ExecLongAsync(string sql, object[] parameters, CancellationToken ct)
    {
        var previous = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(ArchiveJob.CommandTimeoutSeconds);
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql, parameters, ct).ConfigureAwait(false);
        }
        finally
        {
            db.Database.SetCommandTimeout(previous);
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Продовження меж аудиту (arc.usp_EnsureAuditPartitions) не вдалося, помилка SQL {Number}; перевірка запасу pf_ByPeriodKey виконується далі.")]
    private static partial void LogAuditBoundariesFailed(ILogger logger, int number, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Рядки в крайніх партиціях pf_ByPeriodKey: перша (до 202601) — {FirstRows}, остання — {LastRows}. "
            + "Наступний SPLIT перенесе рядки >= межі; що робити — runbook, «edgeRows/refused».")]
    private static partial void LogEdgeRows(ILogger logger, long firstRows, long lastRows);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Архівація аудиту (arc.usp_ArchiveAudit) не вдалася, помилка SQL {Number}.")]
    private static partial void LogAuditArchiveFailed(ILogger logger, int number, Exception ex);

    /// <summary>Чи існує функція партиціонування за періодом.</summary>
    private async Task<bool> PartitionFunctionExistsAsync(CancellationToken ct)
    {
        var found = await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM sys.partition_functions
                WHERE name = N'pf_ByPeriodKey'
                """)

            // Take(1) при COUNT — не оптимізація, а межа, якої вимагає
            // архітектурне правило 6: воно читає інструкцію, а не наміри.
            // `COUNT(*)` повертає рівно один рядок, тож порядок нічого не
            // вибирає — він тут лише для того, щоб `Take` не був без
            // `OrderBy` (EF 10102), а не щоб приглушити попередження.
            .OrderBy(v => v)
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found is [> 0];
    }

    /// <summary>
    /// Рядки в першій і останній партиціях <c>pf_ByPeriodKey</c> — сума по купах і кластерних індексах усіх
    /// таблиць на <c>ps_ByPeriodKey</c> (метадані <c>sys.partitions</c>, без читання таблиць).
    /// </summary>
    private async Task<(long First, long Last)> EdgeRowsAsync(CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<EdgeRow>($"""
                SELECT CAST(CASE WHEN prt.partition_number = 1 THEN 0 ELSE 1 END AS int) AS Edge,
                       CAST(SUM(prt.rows) AS bigint) AS [Rows]
                FROM sys.partitions AS prt
                JOIN sys.indexes AS ix ON ix.object_id = prt.object_id AND ix.index_id = prt.index_id
                JOIN sys.partition_schemes AS ps ON ps.data_space_id = ix.data_space_id
                JOIN sys.partition_functions AS pf ON pf.function_id = ps.function_id
                WHERE ps.name = N'ps_ByPeriodKey' AND ix.index_id IN (0, 1)
                  AND (prt.partition_number = 1 OR prt.partition_number = pf.fanout)
                GROUP BY CASE WHEN prt.partition_number = 1 THEN 0 ELSE 1 END
                """)

            // Рівно два рядки (ліва й права); порядок і межа — правило 6 (EF 10102).
            .OrderBy(r => r.Edge)
            .Take(2)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (rows.Where(r => r.Edge == 0).Sum(r => r.Rows), rows.Where(r => r.Edge == 1).Sum(r => r.Rows));
    }

    /// <summary>Рядок вибірки <see cref="EdgeRowsAsync"/>: 0 — перша партиція, 1 — остання.</summary>
    private sealed record EdgeRow(int Edge, long Rows);

    /// <summary>Скільки меж партиціонування лишилося попереду поточного періоду.</summary>
    private async Task<int> BoundariesAheadAsync(int currentKey, CancellationToken ct)
    {
        var boundaries = await db.Database
            .SqlQuery<int>($"""
                SELECT CAST(rv.value AS int) AS Value
                FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = N'pf_ByPeriodKey'
                """)
            // ⚠ Найстаріші межі нам не потрібні: рахуємо ті, що ПОПЕРЕДУ.
            // Без порядку стеля брала б довільну тисячу, і за понад тисячу
            // меж запас міг би вийти нулем при повній решті (EF 10102).
            .OrderByDescending(v => v)
            .Take(MaxBoundaries)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return boundaries.Count(b => b > currentKey);
    }

    /// <summary>Ключ поточного місяця (R-A6).</summary>
    /// <summary>Ключ поточного місяця для підрахунку запасу партицій.</summary>
    /// <param name="utcNow">Поточний момент у UTC.</param>
    /// <remarks>
    /// ⚠ UTC тут СВІДОМО, а не забутий переклад у пояс майданчика
    /// (<c>H-13</c>, <c>D2-78</c>). Задача обслуговує всю базу — проєкти всіх
    /// майданчиків одразу, — тож «поясу» в неї немає в принципі. А міряє вона
    /// не межу, а ЗАПАС: мінімум дві межі попереду
    /// (<see cref="MinimumBoundariesAhead"/>), тобто щонайменше місяць. Кілька
    /// годин розбіжності на межі місяця цього числа не міняють.
    /// </remarks>
    private static int CurrentPeriodKey(DateTime utcNow) => (utcNow.Year * 100) + utcNow.Month;
}
