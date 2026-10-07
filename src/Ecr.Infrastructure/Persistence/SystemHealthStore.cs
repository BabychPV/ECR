// src/Ecr.Infrastructure/Persistence/SystemHealthStore.cs

using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ISystemHealthStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Віддаються лише числа й мітки часу (див. <see cref="ISystemHealthStore"/>).
/// Два запити до СИСТЕМНИХ уявлень СУБД (<c>msdb</c>, <c>sys.dm_os_volume_stats</c>)
/// потребують прав, яких застосунок може не мати, тож кожен обгорнуто: відмова —
/// <c>null</c> і запис у журнал (лише тип винятку), а не 500 і не текст помилки
/// клієнтові.
/// </remarks>
public sealed partial class SystemHealthStore(
    EcrDbContext db,
    ILogger<SystemHealthStore> logger,
    HealthProbeSql? probes = null,
    HealthCountCache? failedCache = null) : ISystemHealthStore
{
    /// <summary>Вікно «прогалин покриття».</summary>
    private static readonly TimeSpan GapWindow = TimeSpan.FromDays(7);

    /// <summary>Події покриття, після яких дані за інтервал НЕ перенесено.</summary>
    private static readonly string[] GapStatuses =
    [
        CollectionCoverage.SkippedPeriodClosed,
        CollectionCoverage.SkippedPointCeiling,
        CollectionCoverage.SkippedWriteConflict,
        CollectionCoverage.SkippedNeedsConfirmation,
        CollectionCoverage.SkippedDependency,
        CollectionCoverage.SourceDataRefused,
    ];

    /// <summary>
    /// Число активних джерел, у яких ОСТАННІЙ запуск якоїсь сутності — провал. «Останній»
    /// — <c>ROW_NUMBER</c> за (<c>StartedAt DESC, Id DESC</c>): один прохід і один рядок на
    /// сутність замість <c>NOT EXISTS</c> по кожному провалу (O(N·k)). Сталий текст, без вводу.
    /// </summary>
    private const string FailedSourcesSql = """
        SELECT COUNT(DISTINCT s.Id) AS Value
          FROM (SELECT r.SourceEntityId, r.Status,
                       ROW_NUMBER() OVER (PARTITION BY r.SourceEntityId ORDER BY r.StartedAt DESC, r.Id DESC) AS rn
                  FROM itg.CollectionRun AS r) AS x
          JOIN ext.SourceEntity AS e ON e.Id = x.SourceEntityId
          JOIN ext.DataSource AS s ON s.Id = e.DataSourceId
         WHERE x.rn = 1 AND x.Status = 'Failed' AND s.IsActive = 1
        """;

    private readonly HealthProbeSql _probes = probes ?? HealthProbeSql.Default;

    // Без переданого кешу (прямі виклики в тестах) — власний, на екземпляр: між викликами
    // одного store діє, між різними — ні. У DI — синглтон хоста.
    private readonly HealthCountCache _failedCache = failedCache ?? new HealthCountCache();

    /// <inheritdoc />
    public async Task<SystemHealthSnapshot> ReadAsync(DateTime utcNow, CancellationToken ct)
    {
        var day = utcNow.AddHours(-24);
        var week = utcNow - GapWindow;

        // Один агрегат: лічильники черги (стан Running/Queued — без вікна, Failed — за добу).
        var jobs = await db.JobProgresses.AsNoTracking()
            .Where(p => p.State == "Running" || p.State == "Queued" || (p.State == "Failed" && p.UpdatedAt >= day))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Running = g.Count(p => p.State == "Running"),
                Queued = g.Count(p => p.State == "Queued"),
                Failed = g.Count(p => p.State == "Failed"),
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var active = await db.DataSources.AsNoTracking().CountAsync(s => s.IsActive, ct).ConfigureAwait(false);

        // Активне джерело «провалене», якщо для якоїсь його сутності останній запуск
        // збору завершився провалом (новіших запусків тієї ж сутності немає).
        // ⚠ Дорогий запит (скан усіх запусків) — число кешується на 60 с (HealthCountCache).
        var failedSources = await _failedCache
            .GetOrComputeAsync(
                utcNow,
                token => db.Database.SqlQueryRaw<int>(FailedSourcesSql).SingleAsync(token),
                ct)
            .ConfigureAwait(false);

        var gaps = await db.CollectionCoverages.AsNoTracking()
            .CountAsync(c => c.CoveredTo >= week && c.Status != null && GapStatuses.Contains(c.Status), ct)
            .ConfigureAwait(false);

        // Момент останньої помилки — максимум із трьох джерел правди про провал.
        var lastJobError = await db.JobProgresses.AsNoTracking()
            .Where(p => p.State == "Failed")
            .MaxAsync(p => (DateTime?)p.UpdatedAt, ct)
            .ConfigureAwait(false);
        var lastRunError = await db.CollectionRuns.AsNoTracking()
            .Where(r => r.Status == "Failed")
            .MaxAsync(r => r.FinishedAt, ct)
            .ConfigureAwait(false);
        var lastScheduleError = await db.CollectionSchedules.AsNoTracking()
            .MaxAsync(s => s.LastErrorAt, ct)
            .ConfigureAwait(false);

        var freeGb = await ProbeAsync<long?>(_probes.FreeSpaceGb, ct).ConfigureAwait(false);
        var backup = await ProbeAsync<DateTime?>(_probes.LastBackupUtc, ct).ConfigureAwait(false);

        return new SystemHealthSnapshot(
            new HealthJobCounts(jobs?.Running ?? 0, jobs?.Queued ?? 0, jobs?.Failed ?? 0),
            new HealthSourceCounts(active, failedSources, gaps),
            freeGb is { } gb ? Math.Max(0, gb) : null,
            backup is { } b ? DateTime.SpecifyKind(b, DateTimeKind.Utc) : null,
            Latest(lastJobError, lastRunError, lastScheduleError));
    }

    private static DateTime? Latest(params DateTime?[] values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();

        return present.Count == 0
            ? null
            : DateTime.SpecifyKind(present.Max(), DateTimeKind.Utc);
    }

    /// <summary>Скалярний запит до системного уявлення; відмова або відсутність права — <c>null</c>.</summary>
    private async Task<T?> ProbeAsync<T>(string sql, CancellationToken ct)
    {
        try
        {
            // ⚠ `SELECT … AS Value` — вимога `SqlQueryRaw` для скалярів.
            return await db.Database.SqlQueryRaw<T>(sql).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⛔ Лише ТИП винятку: текст помилки SQL Server містить ім'я сервера, бази
            // чи об'єкта, і це не те, що має опинитися навіть в журналі «для зручності».
            LogProbeSkipped(logger, ex.GetType().Name);

            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Health probe skipped: {ExceptionType}.")]
    private static partial void LogProbeSkipped(ILogger logger, string exceptionType);
}

/// <summary>
/// Тексти двох зондів до системних уявлень СУБД. Окремий тип — щоб тест міг підставити
/// запит, який падає, і довести, що відмова дає <c>null</c>, а не виняток.
/// </summary>
/// <param name="FreeSpaceGb">Скаляр <c>Value</c>: вільні ГБ найповнішого диска файлів даних поточної БД.</param>
/// <param name="LastBackupUtc">Скаляр <c>Value</c>: завершення останньої повної/різницевої копії поточної БД, UTC.</param>
public sealed record HealthProbeSql(string FreeSpaceGb, string LastBackupUtc)
{
    /// <summary>Робочі запити.</summary>
    /// <remarks>
    /// ⚠ <c>sys.dm_os_volume_stats</c> від поточної БД (<c>DB_ID()</c>) — без шляху чи
    /// літери диска у виході; потребує <c>VIEW SERVER STATE</c>. <c>msdb.dbo.backupset</c>
    /// пише локальний час сервера — перераховується в UTC різницею <c>GETDATE</c> і
    /// <c>GETUTCDATE</c>. Копії різницевого типу (<c>I</c>) рахуються, журнал (<c>L</c>) — ні.
    /// </remarks>
    public static HealthProbeSql Default { get; } = new(
        """
        SELECT CAST(MIN(vs.available_bytes) / 1073741824 AS bigint) AS Value
          FROM sys.database_files AS f
         CROSS APPLY sys.dm_os_volume_stats(DB_ID(), f.file_id) AS vs
         WHERE f.type = 0
        """,
        """
        SELECT DATEADD(MINUTE, DATEDIFF(MINUTE, GETDATE(), GETUTCDATE()), MAX(b.backup_finish_date)) AS Value
          FROM msdb.dbo.backupset AS b
         WHERE b.database_name = DB_NAME() AND b.type IN ('D', 'I')
        """);
}
