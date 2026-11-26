using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// ✎ b14 document-move (рішення людини 25.11, квіз 2 п.2; D-577): «вікно редагування = рік проєкту + зміщення» — стан
/// річних даних шаблону з прапорцем <c>cfg.Template.YearEditWindow</c> (HSE401) у місячному проєкті.
/// </summary>
/// <remarks>
/// <para>
/// Людина: «для неї діє діапазон з проекту рік + зміщення, після цього редагування не доступне, бо вже відбудеться
/// архівування; це має бути параметр налаштування в шаблоні». Тому стан — НЕ стан січня-якоря (закритий у
/// LANDUI2026) і не агрегат місяців, а функція від меж року проєкту (<c>Project.PeriodStart…PeriodEnd</c>) і того
/// самого річного пільгового вікна (<see cref="YearGraceWindow"/>), яким користуються стани періодів і ворота
/// архівації (<c>ArchiveJob</c>): архівація не починається, доки вікно триває, і закриває рік рівно тоді, коли вона
/// вже дозволена.
/// </para>
/// <para>
/// До початку року — <see cref="PeriodState.Scheduled"/> (правка ще не відкрита, як у календарі проєкту); рік —
/// <see cref="PeriodState.Open"/>; після 31.12 і до кінця зміщення — <see cref="PeriodState.Grace"/> (пізня правка,
/// як в усього року в цьому вікні, D-204); далі — <see cref="PeriodState.Closed"/>. Архівований чи той, що
/// архівується, проєкт відхиляють окремі правила (<c>EditRules</c>: <c>ProjectArchived</c>, <c>ArchivingInProgress</c>).
/// </para>
/// </remarks>
public static class YearEditWindowRule
{
    /// <summary>Стан річних даних на момент <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">Момент рішення (UTC).</param>
    /// <param name="projectStart"><c>Project.PeriodStart</c> (01.01).</param>
    /// <param name="projectEnd"><c>Project.PeriodEnd</c> (31.12).</param>
    /// <param name="yearGraceOffsetDays"><c>Project.YearGraceOffsetDays</c> — «зміщення».</param>
    /// <param name="siteTimeZone">Пояс майданчика проєкту.</param>
    /// <returns>Стан, яким правило правки читає річні дані.</returns>
    public static PeriodState State(
        DateTime nowUtc, DateOnly projectStart, DateOnly projectEnd, int yearGraceOffsetDays, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        var startUtc = Period.UtcBounds(projectStart, projectStart, siteTimeZone).StartUtc;
        if (nowUtc < startUtc)
        {
            return PeriodState.Scheduled;
        }

        var window = YearGraceWindow.For(projectEnd, yearGraceOffsetDays, siteTimeZone);
        if (nowUtc < window.YearEndUtc)
        {
            return PeriodState.Open;
        }

        return nowUtc < window.EndsAtUtc ? PeriodState.Grace : PeriodState.Closed;
    }
}
