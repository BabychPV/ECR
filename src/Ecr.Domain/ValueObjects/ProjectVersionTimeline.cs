namespace Ecr.Domain.ValueObjects;

/// <summary>Рядок часової шкали версій: з періоду <paramref name="FromPeriodKey"/> діє версія <paramref name="TemplateVersionId"/>.</summary>
/// <param name="FromPeriodKey">Перший період дії (<c>Рік*100+№</c>).</param>
/// <param name="TemplateVersionId">Версія шаблону.</param>
public readonly record struct TimelineEntry(int FromPeriodKey, int TemplateVersionId);

/// <summary>
/// Чиста логіка часової шкали версій шаблону проєкту (RPT-STABLE Part B): ОДНА функція «версія періоду».
/// </summary>
/// <remarks>
/// ⛔ Єдине місце, де вирішується «яка версія діє за період». Усі читання/створення за період ідуть через неї
/// (<c>IProjectVersionTimeline</c>) або через її SQL-відповідник <c>doc.fn_VersionOf</c> (паритет доводить тест).
/// </remarks>
public static class ProjectVersionTimeline
{
    /// <summary>
    /// Версія, що діє за період: найбільший <c>FromPeriodKey ≤ periodKey</c>; період раніше за перший рядок
    /// (або порожня шкала) — перший рядок / <paramref name="currentVersionId"/>.
    /// </summary>
    /// <param name="sortedAscending">Рядки шкали за зростанням <c>FromPeriodKey</c>.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="currentVersionId">Запасна версія для порожньої шкали (чинна версія проєкту).</param>
    public static int VersionOf(IReadOnlyList<TimelineEntry> sortedAscending, int periodKey, int currentVersionId)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Count == 0)
        {
            return currentVersionId;
        }

        var found = sortedAscending[0].TemplateVersionId;
        for (var i = 0; i < sortedAscending.Count; i++)
        {
            if (sortedAscending[i].FromPeriodKey <= periodKey)
            {
                found = sortedAscending[i].TemplateVersionId;
            }
            else
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Ефективний початок переносу «з місяця X» (рішення людини 30.10): якщо в проєкті є таблиця з місяцями в колонках
    /// (Q-17, сімейство року) і X не січень — увесь рік переходить на нову версію з січня цього року.
    /// </summary>
    /// <param name="requestedFromPeriodKey">Запитаний період X.</param>
    /// <param name="hasMonthsInColumns">У проєкті є таблиця <c>MonthsInColumns</c>.</param>
    /// <returns>Ефективний початок і ознака, що його зсунуто на січень.</returns>
    public static (int EffectiveFromPeriodKey, bool ShiftedToJanuary) EffectiveFrom(int requestedFromPeriodKey, bool hasMonthsInColumns)
    {
        var key = new PeriodKey(requestedFromPeriodKey);
        if (!hasMonthsInColumns || key.Sequence <= 1)
        {
            return (requestedFromPeriodKey, false);
        }

        return (key.Year * 100 + 1, true);
    }
}
