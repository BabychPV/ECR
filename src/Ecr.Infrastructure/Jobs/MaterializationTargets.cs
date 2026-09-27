// src/Ecr.Infrastructure/Jobs/MaterializationTargets.cs
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
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
internal sealed record MaterializationTarget(
    int SourceEntityId,
    int ProjectId,
    long DocumentId,
    long TableInstanceId,
    int PeriodKey,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string? TimeZoneId)
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
            x.PeriodKeyValue, x.PeriodStart, x.PeriodEnd, x.TimeZoneId))];
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
