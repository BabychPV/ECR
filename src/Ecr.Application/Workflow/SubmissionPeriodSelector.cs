// src/Ecr.Application/Workflow/SubmissionPeriodSelector.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Workflow;

/// <summary>
/// Вибір періоду подання аркуша (D-441) — чиста функція над календарем подання.
/// </summary>
/// <remarks>
/// ⛔ D-441 (слово людини 21.10): «Подати» подає <b>найраніший неподаний період у стані <c>Open</c> або
/// <c>Grace</c></b>, а не поточний. До цього клієнт брав період з адреси або «поточний» (найраніший
/// <c>Open</c>, інакше останній <c>Grace</c>, <c>PeriodStateCalculator.SelectCurrentPeriod</c>) — у лютому
/// кнопка подавала лютий, і неподаний січень (<c>Grace</c>) лишався забутим.
///
/// ⚠ «Неподаний» — аркуш у <c>Draft</c> або <c>Rejected</c> (відхилений чекає повторного подання; саме ці два
/// стани приймає <c>ApprovalState.Submit</c>). <c>Scheduled</c> і <c>Closed</c> не пропонуються ніколи:
/// <c>EditRules.CanSubmit</c> їх відхиляє. Архівний чи архівований проєкт — подати нічого.
/// </remarks>
public static class SubmissionPeriodSelector
{
    /// <summary>Чи приймає подання період у цьому стані (<c>Open</c>/<c>Grace</c>).</summary>
    /// <param name="state">Ефективний стан періоду.</param>
    public static bool AcceptsSubmission(PeriodState state) => state is PeriodState.Open or PeriodState.Grace;

    /// <summary>Чи аркуш у цьому стані ще не подано (<c>Draft</c>/<c>Rejected</c>).</summary>
    /// <param name="status">Стан аркуша за період.</param>
    public static bool IsUnsubmitted(DocumentStatus status) => status is DocumentStatus.Draft or DocumentStatus.Rejected;

    /// <summary>Цільовий період, наступний і перелік відкритих періодів.</summary>
    /// <param name="calendar">Календар подання аркуша.</param>
    /// <returns>Відкриті (<c>Open</c>/<c>Grace</c>) періоди за зростанням з ознакою «можна подати».</returns>
    public static SubmissionChoice Choose(SubmissionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);

        // Архівний проєкт чи архівація — `EditRules.CanSubmit` відхиляє будь-який період.
        var projectAccepts = calendar.ProjectStatus != ProjectStatus.Archived && !calendar.IsArchiving;

        var open = calendar.Periods
            .Where(p => AcceptsSubmission(p.State))
            .OrderBy(p => p.PeriodKey)
            .Select(p => new SubmissionPeriodDto(
                p.PeriodKey, p.State, p.SheetStatus, projectAccepts && IsUnsubmitted(p.SheetStatus)))
            .ToList();

        var submittable = open.Where(p => p.Submittable).Select(p => p.PeriodKey).ToList();

        return new SubmissionChoice(
            submittable.Count > 0 ? submittable[0] : null,
            submittable.Count > 1 ? submittable[1] : null,
            open);
    }

    /// <summary>
    /// Статуси місячних колонок аркуша: колонка місяця k — стан аркуша за період k року
    /// <paramref name="year"/> (D-441).
    /// </summary>
    /// <param name="calendar">Календар подання аркуша.</param>
    /// <param name="sheet">Аркуш шаблону документа.</param>
    /// <param name="year">Рік періоду, який відкрито на екрані.</param>
    /// <param name="canReadTable">Чи бачить читач таблицю (S6); схованої таблиці у відповіді немає.</param>
    /// <remarks>
    /// ⚠ Відповідність «місяць k → період k» — факт моделі Land 7.0, не здогад: проєкт місячний
    /// (<c>Build-LandTemplate.ps1</c>: <c>periodKind = 'Monthly'</c>), колонки таблиць <c>MonthsInColumns</c>
    /// мають <c>IsMonthColumn</c>/<c>MonthNumber</c> 1..12, а формули 7.0 читають колонку місяця k з періоду
    /// <c>[Period:#k]</c> (WW-03, <c>02b-expressions.md</c> §3.2: для місячного календаря <c>#k</c> = місяць k,
    /// <c>PeriodKey.Sequence</c>). Для немісячного календаря номер періоду НЕ дорівнює місяцю (квартал №3 —
    /// не березень), тож статусів там немає взагалі — порожній перелік, а не вигадана відповідність.
    /// </remarks>
    public static IReadOnlyList<MonthColumnStatusDto> MonthColumns(
        SubmissionCalendar calendar, SheetDef sheet, int year, Func<int, bool> canReadTable)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(canReadTable);

        if (calendar.PeriodKind != PeriodKind.Monthly)
        {
            return [];
        }

        var byKey = calendar.Periods.ToDictionary(p => p.PeriodKey);
        var result = new List<MonthColumnStatusDto>();

        foreach (var table in sheet.Tables.Where(t => !t.IsDeleted && t.LayoutKind == TableLayoutKind.MonthsInColumns))
        {
            if (!canReadTable(table.Id))
            {
                continue;
            }

            foreach (var column in table.Columns.Where(c => !c.IsDeleted && c.IsMonthColumn))
            {
                if (column.MonthNumber is not { } month)
                {
                    continue;
                }

                var key = (year * 100) + month;
                if (!byKey.TryGetValue(key, out var period))
                {
                    continue;
                }

                result.Add(new MonthColumnStatusDto(table.Id, column.Code, month, key, period.SheetStatus));
            }
        }

        return result;
    }
}

/// <summary>Вибір періоду подання.</summary>
/// <param name="TargetPeriodKey">Найраніший неподаний <c>Open</c>/<c>Grace</c>; <c>null</c> — подавати нічого.</param>
/// <param name="NextPeriodKey">Наступний неподаний після цільового (попередження «наступний — …»).</param>
/// <param name="Periods">Відкриті періоди за зростанням.</param>
public sealed record SubmissionChoice(
    int? TargetPeriodKey,
    int? NextPeriodKey,
    IReadOnlyList<SubmissionPeriodDto> Periods);
