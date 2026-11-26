using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// ✎ b14 (D-579, огляд H-2): <see cref="IYearEditWindowFacts"/> — один запит (документ × проєкт × прапорець шаблону) і
/// чисте правило <see cref="Domain.Services.YearEditWindowRule"/>. Ті самі факти бере правка й перерахунок.
/// </summary>
/// <param name="db">Контекст.</param>
/// <param name="clock">Годинник (момент рішення).</param>
public sealed class YearEditWindowFacts(EcrDbContext db, IClock clock) : IYearEditWindowFacts
{
    /// <inheritdoc />
    public Task<PeriodState?> StateAsync(long documentId, int year, CancellationToken ct)
        => StateAsync(db, documentId, year, clock.UtcNow, ct);

    /// <summary>Те саме для викликачів зі своїм контекстом і моментом (служба доступу, задача перерахунку).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="year">Рік екземпляра.</param>
    /// <param name="nowUtc">Момент рішення.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<PeriodState?> StateAsync(EcrDbContext db, long documentId, int year, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Шаблон документа; без TemplateId — шаблон версії ПЕРІОДУ-якоря (doc.fn_VersionOf), не «чинна» версія проєкту.
        var anchorKey = (year * 100) + 1;
        var facts = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new
                {
                    p.Status,
                    p.PeriodKind,
                    p.YearGraceOffsetDays,
                    p.TimeZoneId,
                    Window = db.Templates.Any(t => t.YearEditWindow
                        && (t.Id == d.TemplateId
                            || (d.TemplateId == null
                                && db.TemplateVersions.Any(v => v.Id == EcrDbContext.VersionOf(d.ProjectId, anchorKey)
                                                                && v.TemplateId == t.Id)))),
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return facts is null
            ? null
            : Decide(facts.Window, facts.Status, facts.PeriodKind, facts.YearGraceOffsetDays, facts.TimeZoneId, year, nowUtc);
    }

    /// <summary>
    /// Спільне рішення (правка — тим самим запитом проєкту, перерахунок — через <see cref="StateAsync(EcrDbContext, long, int, DateTime, CancellationToken)"/>).
    /// </summary>
    /// <param name="window">Шаблон документа має прапорець <c>YearEditWindow</c>.</param>
    /// <param name="status">Стан проєкту.</param>
    /// <param name="kind">Вид періодів проєкту.</param>
    /// <param name="yearGraceOffsetDays">Зміщення року проєкту.</param>
    /// <param name="timeZoneId">Пояс майданчика.</param>
    /// <param name="year">Рік екземпляра.</param>
    /// <param name="nowUtc">Момент рішення.</param>
    /// <returns>Стан вікна; <c>null</c> — вікно не діє (агрегат місяців, як доти).</returns>
    public static PeriodState? Decide(
        bool window, ProjectStatus status, PeriodKind kind, int yearGraceOffsetDays, string timeZoneId, int year, DateTime nowUtc)
        // ⚠ Як стани місяців: за датою — лише активний місячний проєкт; чернетка лишається на збереженому агрегаті.
        => window && kind == PeriodKind.Monthly && status == ProjectStatus.Active
            ? Domain.Services.YearEditWindowRule.State(nowUtc, year, yearGraceOffsetDays, SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo())
            : null;
}
