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
    /// Стан періоду на момент <paramref name="utcNow"/> — для рішень, які не
    /// можуть чекати годинного прогону <c>PeriodStateJob</c>.
    /// </summary>
    /// <param name="period">Період із обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент у UTC (з <c>IClock</c>).</param>
    /// <remarks>
    /// ⛔ F-08 (UX-PASS, четвертий раунд). Перевідкритий період після
    /// <c>ReopenedUntil</c> лишався відкритим для запису до години: запис
    /// читав ЗБЕРЕЖЕНИЙ стан, а той змінює лише годинна задача. Запис через
    /// 18 с після <c>until</c> проходив із 200, а архівація проєкту
    /// відмовляла «є незакриті періоди». Межа, названа людині, мусить діяти в
    /// ту мить, яку названо, а не «до години потому».
    /// <para>
    /// ⚠ Правило те саме, що в <see cref="Plan"/>, і тому ж місці: розрахунок
    /// <see cref="Calculate"/>, і <c>Closed</c> НІКОЛИ не повертається назад —
    /// відкриває закритий період лише Reopen людини. Інакше збій розрахунку
    /// (межі, яких ще не пораховано) тихо відкривав би закрите.
    /// </para>
    /// <para>
    /// ⚠ Застереження ФВ-1.12 («стан — збережене значення») не скасовано:
    /// збережений стан лишається єдиним, що бачить журнал і перелік періодів.
    /// Тут — лише РІШЕННЯ, що мусить бути правдивим зараз, а не за годину.
    /// </para>
    /// </remarks>
    public PeriodState Effective(Period period, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(period);

        // ⚠ Межі ще не пораховано (календар не будувався) — розраховувати
        // нема з чого: нульові межі дали б `Closed` будь-якому періоду. Тоді
        // рішення бере збережений стан, як і до F-08.
        if (period.State == PeriodState.Closed || period.ComputedCloseAt == default)
        {
            return period.State;
        }

        // ⚠ Лише ВПЕРЕД (`Scheduled → Open → Grace → Closed`), як і переходи
        // самого періоду (`Period.TransitionTo`): рішення не має відкривати те,
        // що збережений стан уже просунув далі, — назад веде лише Reopen.
        var computed = Calculate(period, utcNow, TimeZoneInfo.Utc);

        return computed > period.State ? computed : period.State;
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
        => PlanTransitions(periods, utcNow, siteTimeZone).Transitions;

    /// <summary>
    /// Те саме, що <see cref="Plan"/>, плюс перелік переходів, які розрахунок
    /// дав би НАЗАД і які тому пропущено.
    /// </summary>
    /// <param name="periods">Періоди одного проєкту з уже обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="siteTimeZone">Пояс майданчика (<c>D-68</c>).</param>
    /// <remarks>
    /// ⛔ Пропускалися лише переходи з <c>Closed</c>. Але зворотний розрахунок
    /// дає будь-яка зміна політики, що відсуває межу: подовжили пільговий
    /// строк — період у <c>Grace</c> «мав би» бути <c>Open</c>; відсунули
    /// відкриття — <c>Open</c> «мав би» бути <c>Scheduled</c>. Такий перехід
    /// потрапляв у план, <c>Period.TransitionTo</c> кидав <c>ECR-PRD-0409</c>,
    /// і <c>PeriodStateJob</c> падав на цьому проєкті щогодини, доки дати не
    /// наздоганяли збережений стан.
    /// <para>
    /// ⚠ Пропуск — не тиша: викликач отримує його в
    /// <see cref="PeriodTransitionPlan.Skipped"/> і мусить показати
    /// адміністраторові (задача станів — журналом і <c>itg.MaintenanceRun</c>).
    /// </para>
    /// </remarks>
    public PeriodTransitionPlan PlanTransitions(
        IReadOnlyList<Period> periods, DateTime utcNow, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var transitions = new List<PeriodTransition>();
        var skipped = new List<SkippedPeriodTransition>();

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

            // ⚠ Назад не переводимо НІКОЛИ — ні з `Closed`, ні з будь-якого
            // іншого стану (`Scheduled < Open < Grace < Closed`, як і дозволені
            // переходи `Period.TransitionTo`). `Closed → Grace` — виключно
            // рішення адміністратора через Reopen; збій або зміна розрахунку не
            // мають тихо відкривати закритий період чи повертати пільговий у
            // відкритий.
            if (target < period.State)
            {
                skipped.Add(new SkippedPeriodTransition(period, period.State, target));
                continue;
            }

            transitions.Add(new PeriodTransition(period, target));
        }

        return new PeriodTransitionPlan(transitions, skipped);
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

/// <summary>
/// Перехід, який розрахунок дав би НАЗАД і який тому не застосовано.
/// </summary>
/// <param name="Period">Період.</param>
/// <param name="Current">Збережений стан — він лишається.</param>
/// <param name="Computed">Стан за поточними межами (раніший за збережений).</param>
public readonly record struct SkippedPeriodTransition(Period Period, PeriodState Current, PeriodState Computed)
{
    /// <summary>Пояснення для адміністратора: чому стан не змінено і що з цим робити.</summary>
    public const string Reason =
        "Межі змінились після зміни політики, стан лишається; зворотний перехід — лише ручним Reopen.";
}

/// <summary>Результат планування переходів набору періодів.</summary>
/// <param name="Transitions">Переходи вперед, які треба застосувати.</param>
/// <param name="Skipped">Зворотні переходи, пропущені навмисно.</param>
public sealed record PeriodTransitionPlan(
    IReadOnlyList<PeriodTransition> Transitions,
    IReadOnlyList<SkippedPeriodTransition> Skipped);
