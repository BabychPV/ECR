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
/// (<c>AvailableAt, EnqueueSeq, JobId</c> — ✎ JQ-FIFO). Запарковані (їх робота — в іншій задачі) і невідомі — без місця. Виключення документа, що
/// вже рахується, і межа дітей розкладу місце не змінюють — оцінка, а не обіцянка.
/// </para>
/// <para>
/// Старт = зараз + медіана тривалості КРОКУ за добу (<c>itg.JobProgress</c>: крок ланцюжка — <c>chainElapsedMs</c> ÷
/// зроблені кроки, інша задача — від старту до кінця) × ⌈(кроки попереду + 1 − вільні місця) ÷ місця⌉. ✎ Огляд L3: кроки
/// попереду — ланцюжки рахуються рештою своїх кроків (<c>chainTotal</c> − зроблені; ще не вирішений план — 1 крок), а місця
/// системної задачі — «стеля − 1» (останнє місце — людині). НЕ враховано: решту кроків задач, що виконуються, кроки, які
/// злите натискання ще допише, виключення документа, що рахується, — тому це ОРІЄНТИР. Медіана кешується на 30 с на
/// процес, відповідь на задачу — на 5 с («Мої задачі» опитують до 10 задач).
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
        DECLARE @state nvarchar(32), @laneOf varchar(32), @at datetime2(3), @seq bigint, @manual bit = 0, @rank int = 0;
        SELECT @state = q.[State], @laneOf = q.Lane, @at = q.AvailableAt, @seq = ISNULL(q.EnqueueSeq, 0),
               @manual = CASE WHEN 1 = 1 {{DbJobQueue.ManualFilter}} THEN 1 ELSE 0 END
        FROM itg.JobProgress AS q WHERE q.JobId = @id;
        IF @state = 'Queued' AND @laneOf = @lane AND @at <> {{DbJobQueue.ParkedAt}}
        BEGIN
            DECLARE @manualAhead int = 0, @systemAhead int = 0, @running int, @stepsAhead int = 0;
            SELECT @running = COUNT(*) FROM itg.JobProgress AS r WITH (INDEX(IX_JobProgress_Claim))
             WHERE r.Lane = @lane AND r.Lane IS NOT NULL AND r.[State] = 'Running';
            IF @manual = 1
            BEGIN
                SELECT @rank = {{DbJobQueue.FairRankSql}} FROM itg.JobProgress AS q WHERE q.JobId = @id;
                SELECT @manualAhead = COUNT(*), @stepsAhead = ISNULL(SUM(m.Steps), 0)
                  FROM (SELECT q.JobId, q.AvailableAt, ISNULL(q.EnqueueSeq, 0) AS Seq, {{DbJobQueue.FairRankSql}} AS Rk,
                               CASE WHEN ISJSON(q.Payload) = 1 AND TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int) IS NOT NULL
                                      AND (TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END) > 1
                                     THEN TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END
                                     ELSE 1 END AS Steps
                          FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                         WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                           AND q.JobId <> @id {{DbJobQueue.ManualFilter}}) AS m
                 WHERE m.Rk < @rank OR (m.Rk = @rank AND (m.AvailableAt < @at OR (m.AvailableAt = @at AND (m.Seq < @seq OR (m.Seq = @seq AND m.JobId < @id)))));
            END
            ELSE
            BEGIN
                SELECT @manualAhead = COUNT(*), @stepsAhead = ISNULL(SUM(CASE WHEN ISJSON(q.Payload) = 1 AND TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int) IS NOT NULL
                                      AND (TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END) > 1
                                     THEN TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END
                                     ELSE 1 END), 0)
                  FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                 WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                   {{DbJobQueue.ManualFilter}};
                SELECT @systemAhead = COUNT(*), @stepsAhead = @stepsAhead + ISNULL(SUM(CASE WHEN ISJSON(q.Payload) = 1 AND TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int) IS NOT NULL
                                      AND (TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END) > 1
                                     THEN TRY_CAST(JSON_VALUE(q.Payload, '$.chainTotal') AS int)
                                          - CASE WHEN ISNULL(JSON_QUERY(q.Payload, '$.chainDone'), N'[]') = N'[]' THEN 0
                                                 ELSE 1 + LEN(JSON_QUERY(q.Payload, '$.chainDone'))
                                                      - LEN(REPLACE(JSON_QUERY(q.Payload, '$.chainDone'), N',', N'')) END
                                     ELSE 1 END), 0)
                  FROM itg.JobProgress AS q WITH (INDEX(IX_JobProgress_Claim))
                 WHERE q.Lane = @lane AND q.Lane IS NOT NULL AND q.[State] = 'Queued' AND q.AvailableAt <> {{DbJobQueue.ParkedAt}}
                   AND (q.AvailableAt < @at OR (q.AvailableAt = @at AND (ISNULL(q.EnqueueSeq, 0) < @seq OR (ISNULL(q.EnqueueSeq, 0) = @seq AND q.JobId < @id))))
                   AND NOT (q.CreatedByUserId IS NOT NULL AND q.FanOutParentJobId IS NULL
                            AND ISNULL(CASE WHEN ISJSON(q.Payload) = 1 THEN JSON_VALUE(q.Payload, '$.foreignOwnerPass') END, N'false') <> N'true');
            END
            SELECT @manualAhead + @systemAhead + 1 AS Position, @running AS Running, @stepsAhead AS StepsAhead, CAST(@manual AS int) AS Manual;
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

    /// <summary>Скільки кешується відповідь на задачу.</summary>
    internal static readonly TimeSpan PositionCacheFor = TimeSpan.FromSeconds(5);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, JobQueuePosition Value)> Positions =
        new(StringComparer.Ordinal);

    private static void Remember(string jobId, DateTime now, JobQueuePosition value)
    {
        if (Positions.Count > 2000)
        {
            Positions.Clear();
        }

        Positions[jobId] = (now, value);
    }

    /// <inheritdoc />
    public async Task<JobQueuePosition?> GetAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        // ✎ Огляд M2: «Мої задачі» опитують до 10 задач кожні кілька секунд — відповідь на задачу кешується на 5 с.
        if (Positions.TryGetValue(jobId, out var cached) && clock.UtcNow - cached.At < PositionCacheFor && clock.UtcNow >= cached.At)
        {
            return cached.Value;
        }

        var connection = (SqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        try
        {
            int position, running, stepsAhead;
            bool manual;
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

                (position, running, stepsAhead, manual) =
                    (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3) == 1);
            }

            var now = clock.UtcNow;
            var median = await MedianAsync(connection, now, ct).ConfigureAwait(false);
            var slots = Slots();
            var result = new JobQueuePosition(
                position,
                EstimateStart(stepsAhead + 1, running, manual || slots < 2 ? slots : slots - 1, median, now));
            Remember(jobId, now, result);
            return result;
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

    /// <summary>Лише тести: скинути кеш медіани й відповідей.</summary>
    internal static void ResetMedianCache()
    {
        lock (MedianSync)
        {
            _median = (DateTime.MinValue, null);
        }

        Positions.Clear();
    }
}
