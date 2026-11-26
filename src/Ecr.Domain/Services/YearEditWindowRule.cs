using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// ✎ b14 document-move (рішення людини 25.11, квіз 2 п.2; D-579): «вікно редагування = рік проєкту + зміщення» — стан
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
/// До початку року й увесь рік — <see cref="PeriodState.Open"/> (рішення людини 26.11: «просто таблиця, яку заповнюють»);
/// після 31.12 і до кінця зміщення — <see cref="PeriodState.Grace"/> (пізня правка,
/// як в усього року в цьому вікні, D-204); далі — <see cref="PeriodState.Closed"/>. Архівований чи той, що
/// архівується, проєкт відхиляють окремі правила (<c>EditRules</c>: <c>ProjectArchived</c>, <c>ArchivingInProgress</c>).
/// </para>
/// </remarks>
public static class YearEditWindowRule
{
    /// <summary>Стан річних даних року <paramref name="year"/> на момент <paramref name="nowUtc"/>.</summary>
    /// <param name="nowUtc">Момент рішення (UTC).</param>
    /// <param name="year">Рік ЕКЗЕМПЛЯРА (огляд L-5: не весь проміжок проєкту) — 01.01…31.12 цього року.</param>
    /// <param name="yearGraceOffsetDays"><c>Project.YearGraceOffsetDays</c> — «зміщення».</param>
    /// <param name="siteTimeZone">Пояс майданчика проєкту.</param>
    /// <returns>Стан, яким правило правки й перерахунку читає річні дані.</returns>
    public static PeriodState State(DateTime nowUtc, int year, int yearGraceOffsetDays, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        // ✎ 26.11, людина: «HSE401 — це файл без апрува та надсилання, просто таблиця, яку заповнюють, плюс наявний
        // функціонал» — до початку року дані теж правляться (Open), закриває лише кінець року + зміщення.
        var projectEnd = new DateOnly(year, 12, 31);
        var window = YearGraceWindow.For(projectEnd, yearGraceOffsetDays, siteTimeZone);
        if (nowUtc < window.YearEndUtc)
        {
            return PeriodState.Open;
        }

        return nowUtc < window.EndsAtUtc ? PeriodState.Grace : PeriodState.Closed;
    }
}
