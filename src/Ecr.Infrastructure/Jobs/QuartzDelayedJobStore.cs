// src/Ecr.Infrastructure/Jobs/QuartzDelayedJobStore.cs
using System.Collections.Concurrent;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ D-582: довговічний слід відкладеної постановки Quartz (<c>itg.PendingDelayedJob</c>) — щоб автоперерахунок після
/// правки й ланки джерел, що чекали вікна злиття, пережили перезапуск процесу.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Життєвий цикл рядка: <see cref="SaveAsync"/> — ДО планування триґера; <see cref="UpdateAsync"/> — на кожному злитті
/// (тіло, ранній старт); <see cref="DeleteAsync"/> — коли задача стартує (<see cref="QuartzJobAdapter"/>) чи її знято
/// (скасування, витіснення). Що лишилось — <see cref="ClaimOrphansAsync"/> на старті процесу.
/// </para>
/// <para>
/// ⚠ Запити до бази — лише для задач, які цей процес справді записав (<see cref="Tracked"/>): звичайні постановки (формули
/// після PATCH, натискання) не отримують жодного зайвого походу до СУБД.
/// </para>
/// </remarks>
public sealed class QuartzDelayedJobStore(EcrDbContext db)
{
    /// <summary>Задачі цього процесу, що мають рядок (статично: планувальник scoped, а черга Quartz — на процес).</summary>
    private static readonly ConcurrentDictionary<string, byte> Tracked = new(StringComparer.Ordinal);

    /// <summary>Чи має задача рядок, записаний цим процесом.</summary>
    /// <param name="jobId">Задача.</param>
    public static bool IsTracked(string jobId) => Tracked.ContainsKey(jobId);

    /// <summary>Записує відкладену постановку (до планування триґера).</summary>
    /// <param name="jobId">Задача.</param>
    /// <param name="jobCode">Код задачі.</param>
    /// <param name="payload">Тіло, JSON.</param>
    /// <param name="dueAt">Коли стартувати, UTC.</param>
    /// <param name="createdByUserId">Хто поставив.</param>
    /// <param name="utcNow">Зараз, UTC.</param>
    /// <param name="ct">Скасування.</param>
    public async Task SaveAsync(
        string jobId, string jobCode, string payload, DateTime dueAt, int? createdByUserId, DateTime utcNow, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobCode);
        ArgumentNullException.ThrowIfNull(payload);

        await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO itg.PendingDelayedJob (JobId, JobCode, Payload, DueAt, CreatedByUserId, Revision, CreatedAt)
                VALUES ({jobId}, {jobCode}, {payload}, {dueAt}, {createdByUserId}, 0, {utcNow});
                """,
                ct)
            .ConfigureAwait(false);
        Tracked[jobId] = 0;
    }

    /// <summary>Злиття в задачу, що чекає: нове тіло і, для постановки без відкладення, ранній старт.</summary>
    /// <param name="jobId">Задача.</param>
    /// <param name="payload">Злите тіло; <c>null</c> — тіло не змінилось.</param>
    /// <param name="revision">Номер злиття (<see cref="QuartzPayloadMerges"/>); старіший запис новішого не затирає.</param>
    /// <param name="startNowAt">Момент раннього старту; <c>null</c> — старт не змінився.</param>
    /// <param name="ct">Скасування.</param>
    public async Task UpdateAsync(string jobId, string? payload, long revision, DateTime? startNowAt, CancellationToken ct)
    {
        if (!IsTracked(jobId) || (payload is null && startNowAt is null))
        {
            return;
        }

        if (payload is not null)
        {
            await db.Database.ExecuteSqlAsync(
                    $"""
                    UPDATE itg.PendingDelayedJob SET Payload = {payload}, Revision = {revision}
                    WHERE JobId = {jobId} AND Revision < {revision};
                    """,
                    ct)
                .ConfigureAwait(false);
        }

        if (startNowAt is { } now)
        {
            await db.Database.ExecuteSqlAsync(
                    $"UPDATE itg.PendingDelayedJob SET DueAt = {now} WHERE JobId = {jobId} AND DueAt > {now};", ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Задача стартувала або знята — рядок більше не потрібен.</summary>
    /// <param name="jobId">Задача.</param>
    /// <param name="ct">Скасування.</param>
    public async Task DeleteAsync(string jobId, CancellationToken ct)
    {
        if (!Tracked.TryRemove(jobId, out _))
        {
            return;
        }

        await db.Database.ExecuteSqlAsync($"DELETE FROM itg.PendingDelayedJob WHERE JobId = {jobId};", ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Забирає (видаляє й повертає) постановки, чий процес-власник зник, не стартувавши задачі.
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Постановки, які треба поставити знову.</returns>
    /// <remarks>
    /// <para>
    /// ⚠ Власника видно з <c>itg.JobProgress</c> тієї самої задачі (рядок-дзеркало пишеться ДО цього рядка):
    /// <list type="bullet">
    /// <item><c>Queued</c>/<c>Running</c> — задача жива деінде (інший інстанс) — не чіпаємо;</item>
    /// <item><c>Failed</c> без спроби (<c>Attempt IS NULL</c>) — прибирання на старті закрило чергу процесу, що зник, — ставимо знову;</item>
    /// <item>рядка прогресу немає (збірка без сховища прогресу) — так само ставимо знову;</item>
    /// <item>решта (<c>Succeeded</c>, <c>Cancelled</c>, <c>Failed</c> після старту) — задача вже стартувала чи її зняли, а рядок
    /// лишився (збій видалення) — видаляється БЕЗ повтору: виконана чи злита робота не повторюється.</item>
    /// </list>
    /// </para>
    /// <para>⚠ <c>DELETE … OUTPUT</c> — атомарне взяття: два інстанси, що стартують разом, не поставлять одне двічі.</para>
    /// </remarks>
    public async Task<IReadOnlyList<PendingDelayedJobRow>> ClaimOrphansAsync(CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
                $"""
                DELETE p FROM itg.PendingDelayedJob AS p
                WHERE EXISTS (SELECT 1 FROM itg.JobProgress AS j
                              WHERE j.JobId = p.JobId AND j.[State] NOT IN (N'Queued', N'Running')
                                AND NOT (j.[State] = N'Failed' AND j.Attempt IS NULL));
                """,
                ct)
            .ConfigureAwait(false);

        var claimed = await db.Database
            .SqlQuery<PendingDelayedJobRow>(
                $"""
                DELETE p
                OUTPUT deleted.JobId, deleted.JobCode, deleted.Payload, deleted.DueAt, deleted.CreatedByUserId
                FROM itg.PendingDelayedJob AS p
                WHERE NOT EXISTS (SELECT 1 FROM itg.JobProgress AS j
                                  WHERE j.JobId = p.JobId AND j.[State] IN (N'Queued', N'Running'));
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ L3-01: SqlQuery в ad-hoc тип оминає конвертер UTC — строк приходить Unspecified.
        return [.. claimed.Select(r => r with { DueAt = DateTime.SpecifyKind(r.DueAt, DateTimeKind.Utc) })];
    }

    /// <summary>Повертає рядок, який не вдалося поставити знову (наступний старт спробує ще раз).</summary>
    /// <param name="row">Забраний рядок.</param>
    /// <param name="utcNow">Зараз, UTC.</param>
    /// <param name="ct">Скасування.</param>
    public async Task RestoreAsync(PendingDelayedJobRow row, DateTime utcNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);

        await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO itg.PendingDelayedJob (JobId, JobCode, Payload, DueAt, CreatedByUserId, Revision, CreatedAt)
                VALUES ({row.JobId}, {row.JobCode}, {row.Payload}, {row.DueAt}, {row.CreatedByUserId}, 0, {utcNow});
                """,
                ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Забрана відкладена постановка (<see cref="QuartzDelayedJobStore.ClaimOrphansAsync"/>).</summary>
/// <param name="JobId">Задача процесу, що зник.</param>
/// <param name="JobCode">Код задачі.</param>
/// <param name="Payload">Злите тіло.</param>
/// <param name="DueAt">Коли мала стартувати, UTC.</param>
/// <param name="CreatedByUserId">Хто поставив.</param>
public sealed record PendingDelayedJobRow(string JobId, string JobCode, string Payload, DateTime DueAt, int? CreatedByUserId);
