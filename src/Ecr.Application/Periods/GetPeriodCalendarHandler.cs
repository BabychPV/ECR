using Ecr.Application.Errors;
using Ecr.Application.Periods.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
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
    Common.ICurrentUser currentUser,
    ICampaignSummaryStore sheetCounts)
{
    /// <summary>Повертає календар проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="withSheetCounts">
    /// Додати <c>NotSubmittedSheets</c> до кожного періоду (UI-33, D1). За замовчуванням ні: календар читають на
    /// кожній сторінці документів, а агрегат по аркушах потрібен лише сторінці періодів.
    /// </param>
    public async Task<PeriodCalendarDto> HandleAsync(int projectId, CancellationToken ct, bool withSheetCounts = false)
    {
        // ⛔ Право перевіряється ТУТ (`A7-53`). До цього ендпоінт мав лише
        // `[Authorize]`, тобто оголошене контрактом право не перевіряв ніхто.
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        // ⛔ Q-246: `Document.View` — глобальне право «працює з документами
        // взагалі», не «бачить кожен проєкт» (саме тому `ListProjectsHandler`
        // фільтрує перелік проєктів за грантом). Без гранта будь-хто з
        // `Document.View` бачив повний календар ЧУЖОГО проєкту.
        // ⚠ D-214: календар — рівень документа, його відкриває й роль, звужена
        // аркушами чи періодами (`SeesDocumentsOf` — саме так і рахує
        // `ProjectVisibility`).
        //
        // ⛔ S17: і відмова на чужий проєкт — та сама, що на неіснуючий
        // (`ECR-PRJ-0404`, `ProjectVisibility`), а не `403`: різниця 404/403
        // розповідала, які id проєктів існують.
        ProjectVisibility.RequireVisible(profile, projectId);

        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw ProjectVisibility.NotFound(projectId);

        // ⛔ ФВ-6.14: право — у ЦЬОМУ проєкті.
        Security.PermissionCheck.RequireIn(profile, "Document.View", projectId);

        var zone = TimeZoneInfo.FindSystemTimeZoneById(project.TimeZoneId);

        // ⛔ Q-337, lane 2 UI-аудиту: сторінка Periods показувала «Grace
        // until»/«Range» без жодного зв'язку з чотирма цифрами політики
        // (`Open offset`/`Grace offset`/`Hard-close offset`/`Year grace`) —
        // ярлик `+15/45` у формі створення проєкту показує лише два з
        // чотирьох, і НЕ на цій сторінці взагалі. Політика проєкту вже
        // завантажується для інших операцій (`RecomputeBoundaries` під час
        // побудови календаря) — тут вона потрібна лише для тултипів клієнта,
        // тож окремий запит виправданий саме цим.
        var policy = await periods.GetPolicyAsync(project.PeriodPolicyId, ct).ConfigureAwait(false);

        // ⛔ R-8: не подані аркуші — лише читачу, який бачить усі аркуші проєкту (немає заборон і низьких грантів,
        // проєкт відкритий без звуження ролі); інакше `null`. Запит не виконується взагалі, коли число не віддається.
        var notSubmitted = withSheetCounts && Security.SheetVisibility.SeesAllSheets(profile, projectId)
            ? await sheetCounts.NotSubmittedSheetsByPeriodAsync(projectId, ct).ConfigureAwait(false)
            : null;

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
                p.ReopenedUntil is { } until ? ToSite(until, zone) : null,
                notSubmitted is null ? null : notSubmitted.GetValueOrDefault(p.PeriodKeyValue)))
            .ToList();

        var policyDto = new PeriodPolicyDto(
            policy.Id, policy.Code, policy.OpenOffsetDays, policy.GraceOffsetDays,
            policy.HardCloseOffsetDays, policy.YearGraceOffsetDays);

        return new PeriodCalendarDto(
            project.Id, project.TimeZoneId, project.PeriodKind, project.CurrentPeriodMode, policyDto, items);
    }

    /// <summary>UTC → момент у поясі майданчика зі збереженим зсувом.</summary>
    private static DateTimeOffset ToSite(DateTime utc, TimeZoneInfo zone)
        => TimeZoneInfo.ConvertTime(
            new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)), zone);
}
