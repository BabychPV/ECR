using Ecr.Application.Errors;
using Ecr.Application.Periods.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Errors;

namespace Ecr.Application.Periods;

/// <summary>Календар періодів проєкту для UI (ФВ-1.13).</summary>
/// <remarks>
/// ⚠ Межі віддаються в **поясі майданчика**, а не в UTC. «Кінець січня» на
/// майданчику і в UTC — різні моменти, і саме на цій різниці ламається
/// закриття періоду опівночі (D-68). У базі вони зберігаються в UTC — інакше
/// порівняння в запитах залежало б від поясу; переводить їх саме цей шар,
/// один раз і на межі системи.
/// </remarks>
public sealed class GetPeriodCalendarHandler(
    IPeriodStore periods,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Повертає календар проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PeriodCalendarDto> HandleAsync(int projectId, CancellationToken ct)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        // ⛔ `ECR-PRJ-0404`: суб'єкт відмови — проєкт, а старий `ECR-PRD-0422`
        // обіцяв 422 цифрами і віддавав 404 конвеєром (`P-25`, рядок 4).
        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw new NotFoundException(
                          ErrorCodes.ProjectNotFound, $"Проєкт {projectId} не знайдено.");

        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        var items = project.Periods
            .OrderBy(p => p.PeriodKeyValue)
            .Select(p => new PeriodDto(
                p.Id,
                p.PeriodKeyValue,
                p.PeriodKeyValue / 100,
                p.Sequence,

                // Стан — ЗБЕРЕЖЕНЕ значення (ФВ-1.12): тут воно читається, а не
                // перераховується. Інакше два запити на межі доби показали б
                // різні календарі.
                p.State,
                ToSite(p.ComputedOpenAt, zone),
                ToSite(p.ComputedCloseAt, zone),
                ToSite(p.ComputedGraceAt, zone),

                // Поточний період — підказка UI, а не правило доступу (D-77).
                project.CurrentPeriodId == p.Id,
                p.ReopenedUntil is { } until ? ToSite(until, zone) : null))
            .ToList();

        return new PeriodCalendarDto(
            project.Id, project.TimeZoneId, project.PeriodKind, project.CurrentPeriodMode, items);
    }

    /// <summary>UTC → момент у поясі майданчика зі збереженим зсувом.</summary>
    private static DateTimeOffset ToSite(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), zone);
}
