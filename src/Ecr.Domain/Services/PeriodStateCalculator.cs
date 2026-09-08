using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>
/// Обчислює стан періоду за offsets. Викликається <c>PeriodStateJob</c>,
/// а не запитом: стан періоду — збережене значення, а не функція від
/// <c>now()</c> (ФВ-1.12).
/// </summary>
/// <remarks>
/// ⚠ Різниця принципова. Якби стан рахувався в запиті, два одночасні запити на
/// межі доби дали б різні відповіді, а перевірка доступу — різні рішення для
/// тієї самої комірки. Збережене значення міняє рівно одна задача, і момент
/// зміни видно в журналі.
/// </remarks>
public sealed class PeriodStateCalculator
{
    /// <summary>
    /// Визначає, яким має бути стан періоду на вказаний момент.
    /// </summary>
    /// <param name="period">Період із обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент у UTC (з <c>IClock</c>).</param>
    /// <param name="siteTimeZone">Пояс майданчика: межі — саме в ньому (D-68).</param>
    public PeriodState Calculate(Period period, DateTime utcNow, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        // ⚠ Тимчасове відкриття перевіряється ПЕРШИМ і перекриває розрахунок:
        // адміністратор відкрив період свідомо і з причиною, і повертати його
        // в Closed за розкладом означало б скасувати рішення людини мовчки.
        if (period.ReopenedUntil is { } until && utcNow < until)
        {
            return PeriodState.Grace;
        }

        // Межі вже переведені в UTC у RecomputeBoundaries з поясу майданчика —
        // конвертувати повторно не можна, це зсунуло б їх удруге.
        if (utcNow < period.ComputedOpenAt)
        {
            return PeriodState.Scheduled;
        }

        if (utcNow < period.ComputedGraceAt)
        {
            return PeriodState.Open;
        }

        return utcNow < period.ComputedCloseAt ? PeriodState.Grace : PeriodState.Closed;
    }

    /// <summary>
    /// Обчислює переходи набору періодів, нічого не змінюючи.
    /// </summary>
    /// <param name="periods">Періоди одного проєкту з уже обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="siteTimeZone">Пояс майданчика (<c>D-68</c>).</param>
    /// <remarks>
    /// ⛔ Правило жило в <c>PeriodStateJob.Plan</c> — тобто в
    /// <c>Ecr.Infrastructure</c>, куди прикладний шар не має шляху. Через це
    /// активація проєкту не могла відкрити період САМА і чекала наступного
    /// годинного прогону задачі: щойно активований проєкт годину показував
    /// «період ще не відкрито», хоч за датами він давно відкритий (директива
    /// №09 §7 `W8`, `S-11`). Тепер рішення живе в домені, а задача і обробник
    /// активації беруть його з одного місця.
    /// </remarks>
    public IReadOnlyList<PeriodTransition> Plan(
        IReadOnlyList<Period> periods, DateTime utcNow, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var transitions = new List<PeriodTransition>();

        foreach (var period in periods)
        {
            var target = Calculate(period, utcNow, siteTimeZone);

            // Уже в цільовому стані — не чіпаємо. Повторний прогін має бути
            // безслідним: інакше StateChangedAt оновлювався б щоразу і журнал
            // перестав би відповідати, коли період справді змінився.
            if (target == period.State)
            {
                continue;
            }

            // ⚠ Назад не переводимо НІКОЛИ. `Closed → Grace` — виключно
            // рішення адміністратора через Reopen; збій розрахунку не має
            // тихо відкривати закритий період.
            if (period.State == PeriodState.Closed)
            {
                continue;
            }

            transitions.Add(new PeriodTransition(period, target));
        }

        return transitions;
    }

    /// <summary>
    /// Обирає поточний період проєкту в режимі <c>Auto</c>: найраніший
    /// <c>Open</c>, інакше найпізніший <c>Grace</c>, інакше нічого (D-77).
    /// </summary>
    /// <param name="periods">Періоди проєкту.</param>
    public Period? SelectCurrentPeriod(IReadOnlyList<Period> periods)
    {
        ArgumentNullException.ThrowIfNull(periods);

        // Найраніший Open, а не найпізніший: якщо відкриті два періоди, робота
        // йде в тому, що мав закритися раніше — саме він горить.
        var open = periods
            .Where(p => p.State == PeriodState.Open)
            .OrderBy(p => p.PeriodKeyValue)
            .FirstOrDefault();

        if (open is not null)
        {
            return open;
        }

        // Найпізніший Grace: догортання минулого періоду природно йде від
        // найсвіжішого.
        return periods
            .Where(p => p.State == PeriodState.Grace)
            .OrderByDescending(p => p.PeriodKeyValue)
            .FirstOrDefault();
    }
}

/// <summary>Перехід, який треба застосувати до періоду.</summary>
/// <param name="Period">Період.</param>
/// <param name="Target">Цільовий стан.</param>
public readonly record struct PeriodTransition(Period Period, PeriodState Target);
