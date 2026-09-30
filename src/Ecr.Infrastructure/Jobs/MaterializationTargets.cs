// src/Ecr.Infrastructure/Jobs/MaterializationTargets.cs
using System.Data;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Адресат задачі матеріалізації: пара «сутність джерела + екземпляр таблиці»
/// разом із межами періоду екземпляра.
/// </summary>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період екземпляра.</param>
/// <param name="PeriodStart">Перший день періоду.</param>
/// <param name="PeriodEnd">Останній день періоду, включно.</param>
/// <param name="TimeZoneId">Пояс проєкту.</param>
/// <param name="State">Стан періоду в мить добору.</param>
internal sealed record MaterializationTarget(
    int SourceEntityId,
    int ProjectId,
    long DocumentId,
    long TableInstanceId,
    int PeriodKey,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? TimeZoneId,
    Domain.Enums.PeriodState State)
{
    /// <summary>Межі періоду в UTC у поясі проєкту (<c>D-68</c>, <c>D16-03</c>).</summary>
    public Domain.Entities.Documents.Period.UtcRange UtcBounds()
        => Domain.Entities.Documents.Period.UtcBounds(
            PeriodStart, PeriodEnd, Domain.ValueObjects.SiteTimeZone.Create(TimeZoneId).ToTimeZoneInfo());
}

/// <summary>
/// Добір адресатів матеріалізації — ОДИН запит на обидва шляхи постановки:
/// збір (<see cref="CollectionJob"/>) і перехід періоду
/// (<see cref="MaterializationScheduler"/>).
/// </summary>
/// <remarks>
/// ⛔ Спільний, а не дві копії. Правило «хто отримує задачу» — матеріалізовані
/// мапінги (<c>TargetRowKey</c> і <c>TargetColumnDefId</c> задані, мапінг
/// активний) і лише в таблицю, якій належить колонка мапінгу (суміжне
/// <c>D16-03</c>, 23ad0b34), — розійшлося б між копіями першою ж правкою, і
/// один шлях ставив би задачі, яких інший не ставить.
/// </remarks>
internal static class MaterializationTargets
{
    /// <summary>Адресати, звужені заданими фільтрами; незадані фільтри не звужують.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="sourceEntityId">Лише ця сутність джерела.</param>
    /// <param name="projectId">Лише цей проєкт.</param>
    /// <param name="periodKeys">Лише ці періоди (разом із <paramref name="projectId"/>).</param>
    /// <param name="periodEndNotBefore">Грубий фільтр: період кінчається не раніше цієї дати.</param>
    /// <param name="periodStartNotAfter">Грубий фільтр: період починається не пізніше цієї дати.</param>
    /// <param name="limit">Стеля; найсвіжіші періоди першими.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<List<MaterializationTarget>> FindAsync(
        EcrDbContext db,
        int? sourceEntityId,
        int? projectId,
        IReadOnlyCollection<int>? periodKeys,
        DateOnly? periodEndNotBefore,
        DateOnly? periodStartNotAfter,
        int limit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // ⚠ Мапінги без `TargetRowKey` не матеріалізуються — і це легальний
        // стан (`D-118`): тег може збиратися для звірки, а не для форми.
        //
        // ⚠ Звуження до таблиці — через колонку мапінгу: пара «сутність +
        // екземпляр» існує, лише коли сутність має мапінг саме в таблицю
        // екземпляра.
        var query =
            from m in db.EntityFieldMaps.AsNoTracking()
            from c in db.ColumnDefs.AsNoTracking()
            where m.IsActive
                  && m.TargetRowKey != null
                  && m.TargetColumnDefId != null
                  && c.Id == m.TargetColumnDefId
            join t in db.TableInstances.AsNoTracking() on c.TableDefId equals t.TableDefId
            join d in db.Documents.AsNoTracking() on t.DocumentId equals d.Id
            join p in db.Periods.AsNoTracking()
                on new { d.ProjectId, t.PeriodKeyValue } equals new { p.ProjectId, p.PeriodKeyValue }
            join project in db.Projects.AsNoTracking() on d.ProjectId equals project.Id
            select new
            {
                m.SourceEntityId,
                d.ProjectId,
                t.DocumentId,
                TableInstanceId = t.Id,
                t.PeriodKeyValue,
                p.PeriodStart,
                p.PeriodEnd,
                project.TimeZoneId,
                p.State,
            };

        if (sourceEntityId is { } entity)
        {
            query = query.Where(x => x.SourceEntityId == entity);
        }

        if (projectId is { } projectFilter)
        {
            query = query.Where(x => x.ProjectId == projectFilter);
        }

        if (periodKeys is not null)
        {
            var keys = periodKeys.ToList();
            query = query.Where(x => keys.Contains(x.PeriodKeyValue));
        }

        if (periodEndNotBefore is { } endFrom)
        {
            query = query.Where(x => x.PeriodEnd >= endFrom);
        }

        if (periodStartNotAfter is { } startTo)
        {
            query = query.Where(x => x.PeriodStart <= startTo);
        }

        // ⚠ Distinct: кілька мапінгів однієї сутності в одну таблицю — одна
        // задача на екземпляр, а не по задачі на мапінг.
        //
        // ⚠ Найсвіжіші першими: за переповнення стелі відсікатися мають
        // найстаріші періоди, а не поточний.
        var rows = await query
            .Distinct()
            .OrderByDescending(x => x.PeriodKeyValue)
            .ThenBy(x => x.TableInstanceId)
            .ThenBy(x => x.SourceEntityId)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(x => new MaterializationTarget(
            x.SourceEntityId, x.ProjectId, x.DocumentId, x.TableInstanceId,
            x.PeriodKeyValue, x.PeriodStart, x.PeriodEnd, x.TimeZoneId, x.State))];
    }

    /// <summary>
    /// Лишає адресатів закритих періодів лише тоді, коли в сутності є сирі точки
    /// в межах періоду; решту адресатів повертає без змін.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="targets">Адресати.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Для <c>Closed</c> задача нічого не пише, а лише лишає рядок
    /// <c>SkippedPeriodClosed</c> у журналі покриття. Без точок цей рядок — шум:
    /// активація проєкту з минулими періодами дала б по рядку на кожну пару
    /// «сутність × закритий період» і сховала б справжні пропуски.
    /// <para>
    /// ⚠ Межі — <see cref="MaterializationTarget.UtcBounds"/>, тобто
    /// <c>Period.UtcBounds</c> у поясі проєкту, як у
    /// <c>MaterializeCollectedDataJob</c>; власної арифметики меж тут немає.
    /// </para>
    /// <para>
    /// ⛔ ОДИН запит з ОДНИМ параметром: пари «сутність × період» разом із межами
    /// йдуть JSON-масивом через <c>OPENJSON</c> (як у <c>RuleCoverageReader</c>,
    /// <c>MethodologyStore</c>), на кожну — <c>EXISTS</c> по
    /// <c>ext.RawDataPoint</c>. Попередня форма (гілка <c>EXISTS</c> на період,
    /// зведені <c>UNION ALL</c>) несла 5 параметрів на закритий період і текст,
    /// що ріс лінійно, — активація проєкту з сотнями минулих періодів
    /// наближалася до ліміту SQL Server у 2100 параметрів.
    /// </para>
    /// </remarks>
    public static async Task<List<MaterializationTarget>> KeepClosedWithRawPointsAsync(
        EcrDbContext db,
        IReadOnlyList<MaterializationTarget> targets,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(targets);

        var closed = targets.Where(t => t.State == Domain.Enums.PeriodState.Closed).ToList();
        if (closed.Count == 0)
        {
            return [.. targets];
        }

        // ⚠ Мітки часу — рядком ISO без `Z`: `datetime2` у `OPENJSON ... WITH`
        // не приймає суфікс зони. Межі й так UTC (`Period.UtcBounds`).
        var boundsByPeriod = closed
            .GroupBy(t => (t.ProjectId, t.PeriodKey))
            .ToDictionary(g => g.Key, g => g.First().UtcBounds());

        var pairs = closed
            .Select(t => (t.SourceEntityId, t.ProjectId, t.PeriodKey))
            .Distinct()
            .Select(pair =>
            {
                var bounds = boundsByPeriod[(pair.ProjectId, pair.PeriodKey)];

                return new
                {
                    e = pair.SourceEntityId,
                    p = pair.ProjectId,
                    k = pair.PeriodKey,
                    s = bounds.StartUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                    t = bounds.EndUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                };
            })
            .ToList();

        var pairsJson = new SqlParameter("@pairs", SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(pairs) };

        // ⚠ Результат обмежений входом (не більше рядка на пару), а не `Take`:
        // `Take` без `OrderBy` над сирим SQL дає попередження EF, а порядок тут
        // не має значення. Параметр — `SqlParameter`, тексту з даних у запиті немає.
        var hits = await db.Database
            .SqlQueryRaw<RawPointHit>("""
                SELECT b.SourceEntityId, b.ProjectId, b.PeriodKey
                FROM OPENJSON(@pairs)
                     WITH (SourceEntityId int '$.e',
                           ProjectId int '$.p',
                           PeriodKey int '$.k',
                           StartUtc datetime2(7) '$.s',
                           EndUtc datetime2(7) '$.t') AS b
                WHERE EXISTS (SELECT 1
                              FROM ext.RawDataPoint AS r
                              WHERE r.SourceEntityId = b.SourceEntityId
                                AND r.[Timestamp] >= b.StartUtc
                                AND r.[Timestamp] < b.EndUtc)
                """,
                pairsJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var found = hits
            .Select(h => (h.SourceEntityId, h.ProjectId, h.PeriodKey))
            .ToHashSet();

        return [.. targets.Where(t => t.State != Domain.Enums.PeriodState.Closed
                                      || found.Contains((t.SourceEntityId, t.ProjectId, t.PeriodKey)))];
    }

    /// <summary>Сутність, у якої є сирі точки в межах періоду проєкту.</summary>
    internal sealed class RawPointHit
    {
        public int SourceEntityId { get; init; }

        public int ProjectId { get; init; }

        public int PeriodKey { get; init; }
    }

    /// <summary>Ставить по одній задачі матеріалізації на кожного адресата.</summary>
    /// <param name="jobs">Планувальник.</param>
    /// <param name="targets">Адресати.</param>
    /// <param name="range">
    /// Інтервал для задачі (довідково: згортка йде за межами ПЕРІОДУ, <c>D16-03</c>);
    /// <c>null</c> — межі періоду адресата.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task EnqueueAsync(
        IBackgroundJobScheduler jobs,
        IEnumerable<MaterializationTarget> targets,
        (DateTime From, DateTime To)? range,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(targets);

        foreach (var target in targets)
        {
            var (from, to) = range ?? (target.UtcBounds().StartUtc, target.UtcBounds().EndUtc);

            await jobs
                .EnqueueAsync<IMaterializeCollectedDataJob>(
                    new MaterializeTask(
                        target.SourceEntityId,
                        target.ProjectId,
                        target.DocumentId,
                        target.TableInstanceId,
                        target.PeriodKey,
                        from,
                        to),
                    ct)
                .ConfigureAwait(false);
        }
    }
}
