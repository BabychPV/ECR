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
    /// <param name="yearGrace">
    /// Річне пільгове вікно проєкту (ФВ-1.8); <c>null</c> — без нього, лише
    /// межі самого періоду.
    /// </param>
    /// <remarks>
    /// ⚠ ФВ-1.8, <c>D-204</c> (варіант «б», рішення людини 2026-09-28): у вікні
    /// <c>[кінець року, кінець року + YearGraceOffsetDays)</c> УСІ періоди року
    /// проєкту, що вже пройшли власний пільговий строк, мають стан
    /// <c>Grace</c> — і ті, що на 31.12 ще не закрилися (грудень), і ті, що
    /// закрилися раніше (листопад, січень). Після вікна — <c>Closed</c>. Це
    /// той самий стан <c>Grace</c>, що й у звичайного періоду: запис дозволено
    /// з позначкою <c>IsLateEdit</c> (ФВ-1.9, <see cref="Period.IsLateEditWindow"/>)
    /// — другого механізму немає.
    /// <para>
    /// ⚠ Для збереженого <c>Closed</c> це зворотний перехід. Його застосовує
    /// не <see cref="Period.AdvanceTo"/>, а системний Reopen
    /// (<see cref="PeriodTransitionPlan.YearReopens"/>) — з причиною й
    /// аудитом, як і ручний (ФВ-1.10).
    /// </para>
    /// </remarks>
    public PeriodState Calculate(
        Period period, DateTime utcNow, TimeZoneInfo siteTimeZone, YearGraceWindow? yearGrace = null)
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

        if (utcNow < period.ComputedCloseAt)
        {
            return PeriodState.Grace;
        }

        // ⚠ ФВ-1.8 / D-204: правило «які періоди тримає вікно» — рівно в
        // одному методі, YearGraceWindow.HoldsInGrace.
        return yearGrace is { } year && year.HoldsInGrace(period, utcNow)
            ? PeriodState.Grace
            : PeriodState.Closed;
    }

    /// <summary>
    /// Стан періоду на момент <paramref name="utcNow"/> — для рішень, які не
    /// можуть чекати годинного прогону <c>PeriodStateJob</c>.
    /// </summary>
    /// <param name="period">Період із обчисленими межами.</param>
    /// <param name="utcNow">Поточний момент у UTC (з <c>IClock</c>).</param>
    /// <param name="yearGrace">
    /// Річне вікно проєкту (ФВ-1.8). ⚠ Викликач, що рахує СТАН для задачі, і
    /// викликач, що рахує РІШЕННЯ про запис, мусять передавати одне й те саме
    /// вікно: інакше задача збереже <c>Grace</c>, а рішення за межами періоду
    /// дасть <c>Closed</c> і відмовить у записі, який інтерфейс показує дозволеним.
    /// </param>
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
    /// ⚠ D-204: системний Reopen «вікно року» теж не тут, а в задачі станів
    /// (<see cref="PeriodTransitionPlan.YearReopens"/>): він пише аудит, а
    /// <c>IsLateEdit</c> читає ЗБЕРЕЖЕНИЙ стан. Рішення «можна» при збереженому
    /// <c>Closed</c> дало б запис без позначки пізньої правки. Ціна — до
    /// години після опівночі 31.12 закриті періоди року ще закриті.
    /// </para>
    /// <para>
    /// ⚠ Застереження ФВ-1.12 («стан — збережене значення») не скасовано:
    /// збережений стан лишається єдиним, що бачить журнал і перелік періодів.
    /// Тут — лише РІШЕННЯ, що мусить бути правдивим зараз, а не за годину.
    /// </para>
    /// </remarks>
    public PeriodState Effective(Period period, DateTime utcNow, YearGraceWindow? yearGrace = null)
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
        var computed = Calculate(period, utcNow, TimeZoneInfo.Utc, yearGrace);

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
    /// <param name="yearGrace">Річне вікно проєкту (ФВ-1.8); див. <see cref="Calculate"/>.</param>
    public IReadOnlyList<PeriodTransition> Plan(
        IReadOnlyList<Period> periods, DateTime utcNow, TimeZoneInfo siteTimeZone,
        YearGraceWindow? yearGrace = null)
        => PlanTransitions(periods, utcNow, siteTimeZone, yearGrace).Transitions;

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
    /// <param name="yearGrace">Річне вікно проєкту (ФВ-1.8); див. <see cref="Calculate"/>.</param>
    public PeriodTransitionPlan PlanTransitions(
        IReadOnlyList<Period> periods, DateTime utcNow, TimeZoneInfo siteTimeZone,
        YearGraceWindow? yearGrace = null)
    {
        ArgumentNullException.ThrowIfNull(periods);

        var transitions = new List<PeriodTransition>();
        var skipped = new List<SkippedPeriodTransition>();
        var yearReopens = new List<YearGraceReopen>();

        foreach (var period in periods)
        {
            var target = Calculate(period, utcNow, siteTimeZone, yearGrace);

            // Уже в цільовому стані — не чіпаємо. Повторний прогін має бути
            // безслідним: інакше StateChangedAt оновлювався б щоразу і журнал
            // перестав би відповідати, коли період справді змінився. Зокрема
            // перевідкритий вікном року період далі Grace — другого Reopen і
            // другого запису аудиту немає.
            if (target == period.State)
            {
                continue;
            }

            if (target < period.State)
            {
                // ⚠ D-204, ОКРЕМА явна гілка, не загальний дозвіл зворотних
                // переходів: `Closed → Grace` лише для періоду року проєкту і
                // лише поки триває вікно року. Застосовує її викликач системним
                // Reopen (причина + аудит), а не `AdvanceTo`.
                if (period.State == PeriodState.Closed
                    && target == PeriodState.Grace
                    && yearGrace is { } year
                    && year.HoldsInGrace(period, utcNow))
                {
                    yearReopens.Add(new YearGraceReopen(period, year.EndsAtUtc, year.ReopenReason));
                    continue;
                }

                // ⚠ Решта зворотних — НІКОЛИ (`Scheduled < Open < Grace <
                // Closed`, як і дозволені переходи `Period.TransitionTo`): зміна
                // політики чи збій розрахунку не мають тихо відкривати закритий
                // період чи повертати пільговий у відкритий.
                skipped.Add(new SkippedPeriodTransition(period, period.State, target));
                continue;
            }

            transitions.Add(new PeriodTransition(period, target));
        }

        return new PeriodTransitionPlan(transitions, skipped) { YearReopens = yearReopens };
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

/// <summary>
/// Зворотний перехід <c>Closed → Grace</c> «вікно року» (ФВ-1.8, D-204), який
/// викликач застосовує системним Reopen (<see cref="Period.Reopen"/>) з
/// аудитом, а не <see cref="Period.AdvanceTo"/>.
/// </summary>
/// <param name="Period">Закритий період року проєкту.</param>
/// <param name="Until">До якого моменту відкрито — кінець вікна року (<see cref="YearGraceWindow.EndsAtUtc"/>).</param>
/// <param name="Reason">Причина для <c>Period.ReopenReason</c> і аудиту.</param>
public readonly record struct YearGraceReopen(Period Period, DateTime Until, string Reason);

/// <summary>
/// Річне пільгове вікно проєкту (ФВ-1.8): <c>[кінець року, кінець року +
/// YearGraceOffsetDays днів)</c> у поясі майданчика.
/// </summary>
/// <param name="YearEndUtc">Опівніч після останнього дня проєкту (після 31.12) — початок вікна.</param>
/// <param name="EndsAtUtc">Кінець вікна, виключно.</param>
/// <param name="LastDay">Останній день вікна за майданчиком, включно — для повідомлень.</param>
/// <param name="ProjectEnd">Останній день року проєкту (<c>Project.PeriodEnd</c>): періоди, що кінчаються не пізніше, — «цього року».</param>
/// <remarks>
/// ⚠ «Рік проєкту» — це <c>Project.PeriodStart…Project.PeriodEnd</c>
/// (джерело істини про межі; <c>CreateProjectHandler</c> ставить
/// <c>01.01…31.12</c> звітного року), а НЕ <c>Project.Year</c> — той лише
/// підпис для UI і може бути порожнім.
/// <para>
/// ⚠ Відлік «+45 до 31.12» читається як 45 ПОВНИХ діб після кінця року:
/// 31.12.2026 + 45 → останній день правок 14.02.2027 включно, вікно
/// зачиняється опівночі 15.02 за майданчиком. Саме так читається приклад
/// <c>reference/design/06</c> §ФВ-1.8 («до 14.02.2027 користувачі можуть
/// виправляти»). Нуль днів — вікна немає.
/// </para>
/// <para>
/// ⛔ Одне вікно на двох споживачів: стан періоду (<see cref="PeriodStateCalculator"/>)
/// і ворота фізичної архівації (<c>ArchiveJob</c>, АРХ-1). Два окремі
/// відліки — від 31.12 і від моменту позначки «заархівовано» — уже
/// розходились (архівація чекала від <c>ClosedAt</c>, а редагування року не
/// чекало нічого).
/// </para>
/// </remarks>
public readonly record struct YearGraceWindow(DateTime YearEndUtc, DateTime EndsAtUtc, DateOnly LastDay, DateOnly ProjectEnd)
{
    /// <summary>Вікно для проєкту.</summary>
    /// <param name="projectEnd">Останній день проєкту — <c>Project.PeriodEnd</c> (31.12).</param>
    /// <param name="yearGraceOffsetDays"><c>Project.YearGraceOffsetDays</c>.</param>
    /// <param name="siteTimeZone">Пояс майданчика проєкту (D-68).</param>
    public static YearGraceWindow For(DateOnly projectEnd, int yearGraceOffsetDays, TimeZoneInfo siteTimeZone)
    {
        ArgumentNullException.ThrowIfNull(siteTimeZone);

        // ⚠ Те саме перетворення «опівніч дати в поясі → UTC», що й межі
        // періодів (`Period.UtcBounds`), а не друга арифметика.
        var yearEnd = Period.UtcBounds(projectEnd, projectEnd, siteTimeZone).EndUtc;
        var lastDay = projectEnd.AddDays(Math.Max(0, yearGraceOffsetDays));
        var endsAt = Period.UtcBounds(lastDay, lastDay, siteTimeZone).EndUtc;

        return new YearGraceWindow(yearEnd, endsAt, lastDay, projectEnd);
    }

    /// <summary>Чи триває вікно в указаний момент.</summary>
    /// <param name="utcNow">Момент у UTC.</param>
    public bool Contains(DateTime utcNow) => YearEndUtc <= utcNow && utcNow < EndsAtUtc;

    /// <summary>
    /// Чи тримає вікно період у <c>Grace</c> у вказаний момент — ЄДИНЕ місце
    /// правила «які періоди відкриває вікно року» (D-204).
    /// </summary>
    /// <param name="period">Період проєкту.</param>
    /// <param name="utcNow">Момент у UTC.</param>
    /// <remarks>
    /// ⚠ Варіант «б» (рішення людини 2026-09-28): УСІ періоди цього року
    /// проєкту, а не лише ті, що на 31.12 ще не закрилися, — «нам потрібно
    /// мати можливість змінити дані … і щоб система знов перерахувала до
    /// закінчення нашого періоду». Період наступного року (кінчається після
    /// <see cref="ProjectEnd"/>) вікно не чіпає; поза вікном не чіпає нічого.
    /// </remarks>
    public bool HoldsInGrace(Period period, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(period);

        return Contains(utcNow) && period.PeriodEnd <= ProjectEnd;
    }

    /// <summary>Причина системного Reopen «вікно року» — у <c>Period.ReopenReason</c> і в аудит.</summary>
    public string ReopenReason
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Вікно року (ФВ-1.8, D-204): період року {ProjectEnd:yyyy} відкрито системою для правок і перерахунку до {LastDay:dd.MM.yyyy} включно.");
}

/// <summary>Результат планування переходів набору періодів.</summary>
/// <param name="Transitions">Переходи вперед, які треба застосувати.</param>
/// <param name="Skipped">Зворотні переходи, пропущені навмисно.</param>
public sealed record PeriodTransitionPlan(
    IReadOnlyList<PeriodTransition> Transitions,
    IReadOnlyList<SkippedPeriodTransition> Skipped)
{
    /// <summary>
    /// Закриті періоди року, які вікно року (ФВ-1.8, D-204) відкриває
    /// системним Reopen — з причиною й аудитом (ФВ-1.10).
    /// </summary>
    /// <remarks>
    /// ⚠ Окремо від <see cref="Transitions"/> навмисно: <c>Period.AdvanceTo</c>
    /// зворотного переходу не допускає, і викликач, що знає лише переходи
    /// вперед (активація, <c>PeriodStateCalculator.Plan</c>), їх не
    /// застосує мовчки — лише <c>PeriodStateJob</c>, що пише аудит.
    /// </remarks>
    public IReadOnlyList<YearGraceReopen> YearReopens { get; init; } = [];
}
