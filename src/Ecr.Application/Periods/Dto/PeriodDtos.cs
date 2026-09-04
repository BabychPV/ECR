using Ecr.Domain.Enums;

namespace Ecr.Application.Periods.Dto;

/// <summary>Період у календарі проєкту.</summary>
/// <remarks>
/// ⚠ Межі віддаються як <see cref="DateTimeOffset"/>, а не <c>DateTime</c>,
/// і рахуються в <b>поясі майданчика</b>, а не в UTC: «кінець січня» на
/// майданчику і в UTC — різні моменти, і саме на цій різниці ламається
/// закриття періоду опівночі.
/// </remarks>
/// <param name="Id">Ідентифікатор періоду.</param>
/// <param name="PeriodKey"><c>Year*100 + Sequence</c>; він же ключ партиції.</param>
/// <param name="Year">Рік.</param>
/// <param name="Sequence">Порядковий номер у році: <c>1…12</c> для місячних, <c>1…4</c> для квартальних.</param>
/// <param name="State">Стан; обчислює <c>PeriodStateJob</c>, а не запит.</param>
/// <param name="StartsAt">Початок періоду в поясі майданчика.</param>
/// <param name="EndsAt">Кінець періоду в поясі майданчика, виключно.</param>
/// <param name="GraceEndsAt">Кінець пільгового вікна; після нього період закривається.</param>
/// <param name="IsCurrent">Чи є періодом за замовчуванням для UI.</param>
/// <param name="ReopenedUntil">Якщо період відкрито повторно — до якого моменту.</param>
public sealed record PeriodDto(
    int Id,
    int PeriodKey,
    int Year,
    int Sequence,
    PeriodState State,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset? GraceEndsAt,
    bool IsCurrent,
    DateTimeOffset? ReopenedUntil);

/// <summary>Календар проєкту.</summary>
/// <remarks>
/// <paramref name="CurrentPeriodMode"/> віддається окремо від
/// <c>IsCurrent</c> у періодах, бо це різні речі: режим каже, <b>як</b>
/// обирається поточний період, а прапорець — <b>який саме</b> обрано.
/// ⚠ Поточний період — підказка UI, а не правило доступу (<c>D-77</c>).
/// </remarks>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="TimeZoneId">Пояс майданчика, у якому пораховані межі.</param>
/// <param name="PeriodKind">Періодичність.</param>
/// <param name="CurrentPeriodMode">Автоматичний вибір чи закріплений період.</param>
/// <param name="Periods">Періоди в порядку зростання <c>PeriodKey</c>.</param>
public sealed record PeriodCalendarDto(
    int ProjectId,
    string TimeZoneId,
    PeriodKind PeriodKind,
    CurrentPeriodMode CurrentPeriodMode,
    IReadOnlyList<PeriodDto> Periods);
