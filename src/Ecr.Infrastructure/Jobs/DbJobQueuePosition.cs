// src/Ecr.Infrastructure/Jobs/DbJobQueuePosition.cs
using System.Data;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ PERF-10 (D-564 (в), рішення людини 21.11 §6 п. 3): місце задачі в черзі лейна перерахунку й ОРІЄНТОВНИЙ старт.
/// </summary>
/// <remarks>
/// <para>
/// Місце — той самий порядок, що claim лейна (<see cref="DbJobQueue"/>): спершу ручні (задача людини) — по черзі людей
/// (<see cref="DbJobQueue.FairRankSql"/>: скільки задач тієї самої людини виконується чи стоїть попереду), далі решта FIFO
/// (<c>AvailableAt, JobId</c>). Запарковані (їх робота — в іншій задачі) і невідомі — без місця. Виключення документа, що
/// вже рахується, і межа дітей розкладу місце не змінюють — оцінка, а не обіцянка.
/// </para>
/// <para>
/// Старт = зараз + медіана тривалості КРОКУ за добу (<c>itg.JobProgress</c>: крок ланцюжка — <c>chainElapsedMs</c> ÷
/// зроблені кроки, інша задача — від старту до кінця) × ⌈(місце − вільні місця) ÷ місця⌉; місця — глобальна стеля
/// (<see cref="QuartzJobTypeLimiter.RecalcMaxConcurrent"/>). Медіана кешується на 30 с на процес.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="clock">Годинник (зараз — для старту й кешу медіани).</param>
/// <param name="limits">Межі (глобальна стеля перерахунку).</param>
public sealed class DbJobQueuePosition(EcrDbContext db, Domain.Abstractions.IClock clock, QuartzJobTypeLimiter? limits = null)
    : IJobQueuePosition
{
    /// <summary>Скільки кешується медіана кроку.</summary>
    internal static readonly TimeSpan MedianCacheFor = TimeSpan.FromSeconds(30);

    private static readonly object MedianSync = new();
    private static (DateTime At, double? Ms) _median = (DateTime.MinValue, null);

    /// <summary>Місце задачі <c>@id</c>: <c>NULL</c> рядків — не чекає в лейні.</summary>
    internal static readonly string PositionSql = $$"""
        /* ecr:jobqueue-position */
        SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;
        DECLARE @state nvarchar(32), @laneOf varchar(32), @at datetime2(3), @manual bit = 0, @rank int = 0;
        SELECT @state = q.[State], @laneOf = q.Lane, @at = q.AvailableAt,
               @manual = CASE WHEN 1 = 1 {{DbJobQueue.ManualFilter}} THEN 1 ELSE 0 END
        FROM itg.JobProgress AS q WHERE q.JobId = @id;
        IF @state = 'Queued' AND @laneOf = @lane AND @at <> {{DbJobQueue.ParkedAt}}
        BEGIN
            DECLARE @manualAhead int = 0, @systemAhead int = 0, @running int;
            SELECT @running = COUNT(*) FROM itg.JobProgress AS r WITH (INDEX(IX_JobProgress_Claim))
             WHERE r.Lane = @lane AND r.Lane IS NOT NULL AND r.[State] = 'Running';
            IF @manual = 1
            BEGIN
                SELECT @rank = {{DbJobQueue.FairRankSql}} FROM itg.JobProgress AS q WHERE q.JobId = @id;
                SELECT @manualAhead = COUNT(*)
                  FROM (SELECT q.JobId, q.AvailableAt, {{DbJobQueue.FairRankSql}} AS Rk
                          FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                         WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                           AND q.JobId <> @id {{DbJobQueue.ManualFilter}}) AS m
                 WHERE m.Rk < @rank OR (m.Rk = @rank AND (m.AvailableAt < @at OR (m.AvailableAt = @at AND m.JobId < @id)));
            END
            ELSE
            BEGIN
                SELECT @manualAhead = COUNT(*) FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                 WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                   {{DbJobQueue.ManualFilter}};
                SELECT @systemAhead = COUNT(*) FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                 WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                   AND (q.AvailableAt < @at OR (q.AvailableAt = @at AND q.JobId < @id))
                   AND NOT (q.CreatedByUserId IS NOT NULL AND q.FanOutParentJobId IS NULL
                            AND ISNULL(CASE WHEN ISJSON(q.Payload) = 1 THEN JSON_VALUE(q.Payload, '$.foreignOwnerPass') END, N'false') <> N'true');
            END
            SELECT @manualAhead + @systemAhead + 1 AS Position, @running AS Running;
        END
        """;

    /// <summary>Медіана тривалості кроку лейна за добу, мс.</summary>
    internal const string MedianSql = """
        /* ecr:jobqueue-step-median */
        SELECT TOP (1) PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY s.Ms) OVER () AS Ms
          FROM (SELECT TOP (200)
                       CASE WHEN ISJSON(j.Payload) = 1 AND TRY_CAST(JSON_VALUE(j.Payload, '$.chainElapsedMs') AS bigint) > 0
                                 AND ISNULL(JSON_QUERY(j.Payload, '$.chainDone'), N'[]') <> N'[]'
                            THEN TRY_CAST(JSON_VALUE(j.Payload, '$.chainElapsedMs') AS bigint)
                                 / (1 + LEN(JSON_QUERY(j.Payload, '$.chainDone')) - LEN(REPLACE(JSON_QUERY(j.Payload, '$.chainDone'), N',', N'')))
                            ELSE DATEDIFF_BIG(millisecond, j.StartedAt, j.UpdatedAt) END AS Ms
                  FROM itg.JobProgress AS j
                 WHERE j.Lane = @lane AND j.Lane IS NOT NULL AND j.[State] = 'Succeeded' AND j.DocumentId > 0
                   AND j.UpdatedAt >= DATEADD(day, -1, SYSUTCDATETIME())
                 ORDER BY j.UpdatedAt DESC) AS s;
        """;

    /// <inheritdoc />
    public async Task<JobQueuePosition?> GetAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            int position, running;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = PositionSql;
                command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction() as SqlTransaction;
                command.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = jobId;
                command.Parameters.Add("@lane", SqlDbType.VarChar, JobLanes.MaxLength).Value = JobLanes.Recalc;
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                (position, running) = (reader.GetInt32(0), reader.GetInt32(1));
            }

            var now = clock.UtcNow;
            var median = await MedianAsync(connection, now, ct).ConfigureAwait(false);
            return new JobQueuePosition(position, EstimateStart(position, running, Slots(), median, now));
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Орієнтовний старт: вільні місця беруть перших; решта чекає ⌈(місце − вільні) ÷ місця⌉ кроків по медіані.
    /// </summary>
    /// <param name="position">Місце (1 — наступна).</param>
    /// <param name="running">Скільки виконується зараз.</param>
    /// <param name="slots">Місць (глобальна стеля).</param>
    /// <param name="medianMs">Медіана кроку, мс; <c>null</c> — оцінки немає.</param>
    /// <param name="now">Зараз (UTC).</param>
    internal static DateTime? EstimateStart(int position, int running, int slots, double? medianMs, DateTime now)
    {
        if (medianMs is not { } ms || slots < 1)
        {
            return null;
        }

        var free = Math.Max(0, slots - running);
        var waves = position <= free ? 0 : (int)Math.Ceiling((position - free) / (double)slots);
        return DateTime.SpecifyKind(now.AddMilliseconds(waves * ms), DateTimeKind.Utc);
    }

    private int Slots() => limits?.RecalcMaxConcurrent is > 0 and var cap ? cap : QuartzJobTypeLimiter.DefaultRecalcMaxConcurrent;

    private static async Task<double?> MedianAsync(SqlConnection connection, DateTime now, CancellationToken ct)
    {
        lock (MedianSync)
        {
            if (now - _median.At < MedianCacheFor && now >= _median.At)
            {
                return _median.Ms;
            }
        }

        await using var command = connection.CreateCommand();
        command.CommandText = MedianSql;
        command.Parameters.Add("@lane", SqlDbType.VarChar, JobLanes.MaxLength).Value = JobLanes.Recalc;
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        double? ms = value is null or DBNull ? null : Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);

        lock (MedianSync)
        {
            _median = (now, ms);
        }

        return ms;
    }

    /// <summary>Лише тести: скинути кеш медіани.</summary>
    internal static void ResetMedianCache()
    {
        lock (MedianSync)
        {
            _median = (DateTime.MinValue, null);
        }
    }
}
