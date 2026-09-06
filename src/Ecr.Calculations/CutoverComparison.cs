using System.Globalization;

namespace Ecr.Calculations;

/// <summary>
/// Категорія рядка звіту золотої звірки (<c>E-6</c>, <c>H-24d-2</c>).
/// </summary>
/// <remarks>
/// ⛔ Категорій рівно **три** плюс збіг, і третя з'явилася не для повноти.
/// <c>StagesBase.cs:179-183</c> чинної збірки містить порожній
/// <c>catch (Exception) { }</c> із закоментованим логуванням, а фаза 2
/// обмежена ста ітераціями і виходить мовчки. Отже там, де чинна система не
/// порахувала, наш рушій дасть **або число, або явну помилку** — і те, й те
/// розійдеться з еталоном, і те, й те є **покращенням**.
///
/// ⚠ Без окремої категорії наші покращення лежали б у звіті поряд із нашими
/// ж дефектами, і хтось витратив би тиждень, доводячи, що це не дефекти.
/// </remarks>
public enum CutoverCategory
{
    /// <summary>Числа збігаються після округлення до подання колонки.</summary>
    Match = 0,

    /// <summary>
    /// Чинна система не дала результату, наш рушій дав.
    /// </summary>
    /// <remarks>
    /// ⛔ Це **покращення**, а не розбіжність до розбору. Cutover не блокує.
    /// </remarks>
    LegacySilent = 1,

    /// <summary>
    /// Після округлення числа рівні, до округлення — ні.
    /// </summary>
    /// <remarks>
    /// Наслідок <c>float</c>-джерел чинної системи. Логується, пояснюється
    /// покейсно, cutover **не** блокує (<c>B13</c> §8, <c>ER-C-06</c>).
    /// </remarks>
    ExplainCaseByCase = 2,

    /// <summary>Числа різні і після округлення — <b>блокує cutover</b>.</summary>
    Blocking = 3,
}

/// <summary>
/// Вердикт звірки однієї величини: наше число проти числа чинної системи.
/// </summary>
/// <param name="Category">Куди рядок іде у звіті.</param>
/// <param name="Legacy">Число чинної системи; <c>null</c> — результату не було.</param>
/// <param name="Actual">Наше число; <c>null</c> — результату немає.</param>
/// <param name="RoundedLegacy">Число чинної системи в поданні колонки.</param>
/// <param name="RoundedActual">Наше число в поданні колонки.</param>
/// <param name="Scale">Знаків, у яких колонка подається; <c>null</c> — невідомо.</param>
public sealed record CutoverVerdict(
    CutoverCategory Category,
    decimal? Legacy,
    decimal? Actual,
    decimal? RoundedLegacy,
    decimal? RoundedActual,
    int? Scale);

/// <summary>Підсумок звірки: скільки рядків у кожній категорії.</summary>
/// <param name="Match">Збіглося.</param>
/// <param name="LegacySilent">Чинна система не дала результату.</param>
/// <param name="ExplainCaseByCase">Розбіжність до округлення — розбір покейсно.</param>
/// <param name="Blocking">Розбіжність після округлення.</param>
public sealed record CutoverSummary(
    int Match,
    int LegacySilent,
    int ExplainCaseByCase,
    int Blocking)
{
    /// <summary>Скільки величин звірено всього.</summary>
    public int Total => Match + LegacySilent + ExplainCaseByCase + Blocking;

    /// <summary>
    /// Чи можна вмикати новий рушій на цьому розділі.
    /// </summary>
    /// <remarks>
    /// ⛔ Порожня звірка — **НЕ дозвіл**, рівно як порожній золотий набір не
    /// зелений (<see cref="Ecr.Application.Calculations.GoldenSet"/> не
    /// посилається сюди лише тому, що лежить шаром вище). «Звіряти не було
    /// чого, отже все гаразд» — та сама підміна, через яку cutover без
    /// перевірки виглядав би як cutover з перевіркою.
    ///
    /// ⚠ <see cref="ExplainCaseByCase"/> і <see cref="LegacySilent"/> тут
    /// **навмисно** не рахуються. Перше — наслідок <c>float</c>-джерел
    /// еталона, друге — наше покращення; блокувати cutover через них означало
    /// б вимагати від нового рушія відтворити дефекти старого.
    /// </remarks>
    public bool AllowsCutover => Total > 0 && Blocking == 0;
}

/// <summary>
/// Критерій приймання нового рушія (<c>ФВ-9.16</c>, <c>E-6</c>, <c>ER-C-06</c>).
/// </summary>
/// <remarks>
/// ⛔ Формулювання «побітово сумісні з чинною системою» знято директивою №05
/// §8, і не заради послаблення. Дослівно з <c>B13</c> §8: «побайтна рівність
/// двох різних кодових шляхів на <c>double</c> недосяжна в принципі, а
/// критерій, який неможливо виконати, перестають перевіряти». Чинний рушій
/// рахує в <c>double</c>, наш <c>Strict</c> — у <c>decimal</c>; вимагати збігу
/// всіх сімнадцяти знаків означало б гарантований червоний звіт на кожному
/// рядку і — через тиждень — звіт, який ніхто не відкриває.
///
/// ⚠ Здійсненне формулювання: **допуск нуль ПІСЛЯ округлення до знаків, у
/// яких колонка подається** (<c>ColumnDef.DisplayFormat</c>). Тобто рівність
/// вимагається рівно там, де число бачить людина, і рівно та, яку можна
/// виконати.
/// </remarks>
public static class CutoverComparison
{
    /// <summary>
    /// Звіряє одну величину з еталоном чинної системи.
    /// </summary>
    /// <param name="legacy">Число чинної системи; <c>null</c> — результату не було.</param>
    /// <param name="actual">Наше число; <c>null</c> — результату немає.</param>
    /// <param name="displayFormat">Формат подання колонки, напр. <c>#,##0.00</c>.</param>
    /// <param name="columnScale">Знаків колонки — запасний варіант, коли формату немає.</param>
    /// <returns>Вердикт із обома числами і їхнім поданням.</returns>
    public static CutoverVerdict Compare(
        decimal? legacy, decimal? actual, string? displayFormat, int? columnScale = null)
    {
        var scale = ScaleOf(displayFormat) ?? columnScale;

        // ⛔ Порядок перевірок не переставляти. Відсутність еталона питається
        // ПЕРШОЮ: інакше рядок, якого чинна система не порахувала, потрапив би
        // у «блокує cutover» — тобто наше покращення пішло б у звіт як наш
        // дефект, і саме проти цього написано `H-24d-2`.
        if (legacy is null)
        {
            // Обидва мовчать — звіряти нічого і повідомляти нема про що.
            return actual is null
                ? new CutoverVerdict(CutoverCategory.Match, null, null, null, null, scale)
                : new CutoverVerdict(
                    CutoverCategory.LegacySilent, null, actual, null, Round(actual, scale), scale);
        }

        // ⚠ Зворотний випадок покращенням не є: чинна система дала число, ми
        // не дали. Це втрата результату, і вона блокує cutover.
        if (actual is null)
        {
            return new CutoverVerdict(
                CutoverCategory.Blocking, legacy, null, Round(legacy, scale), null, scale);
        }

        var roundedLegacy = Round(legacy, scale);
        var roundedActual = Round(actual, scale);

        // ⛔ Порівнюються ОКРУГЛЕНІ числа, і саме в цьому вся поправка. Тут
        // стояло б `legacy.Value == actual.Value`, і воно було б тим самим
        // невиконанним критерієм: `double`-джерело еталона розходиться з нашим
        // `decimal` у знаках, яких ніхто ніколи не бачить.
        var category = roundedLegacy == roundedActual
            ? legacy.Value == actual.Value
                ? CutoverCategory.Match
                : CutoverCategory.ExplainCaseByCase
            : CutoverCategory.Blocking;

        return new CutoverVerdict(
            category, legacy, actual, roundedLegacy, roundedActual, scale);
    }

    /// <summary>Рахує вердикти за категоріями.</summary>
    /// <param name="verdicts">Вердикти звірки розділу.</param>
    /// <returns>Підсумок із відповіддю про cutover.</returns>
    public static CutoverSummary Summarize(IEnumerable<CutoverVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        int match = 0, silent = 0, explain = 0, blocking = 0;

        foreach (var verdict in verdicts)
        {
            switch (verdict.Category)
            {
                case CutoverCategory.Match: match++; break;
                case CutoverCategory.LegacySilent: silent++; break;
                case CutoverCategory.ExplainCaseByCase: explain++; break;
                case CutoverCategory.Blocking: blocking++; break;
                default: break;
            }
        }

        return new CutoverSummary(match, silent, explain, blocking);
    }

    /// <summary>
    /// Скільки знаків після коми показує формат подання.
    /// </summary>
    /// <param name="displayFormat">Формат колонки; <c>null</c> — формату немає.</param>
    /// <returns>Кількість знаків або <c>null</c>, якщо формат її не задає.</returns>
    /// <remarks>
    /// ⛔ <c>null</c> — це «формат не каже», а не «нуль знаків». Різниця не
    /// теоретична: повернути тут нуль означало б звіряти обидва числа з
    /// точністю до одиниць і оголосити збіг там, де розходяться копійки.
    ///
    /// ⚠ Відсоток додає **два** знаки, проміле — три: <c>0.00%</c> показує
    /// <c>0.12345</c> як «12.35 %», тобто подає чотири знаки вихідного числа,
    /// а не два. Порахувати тут два означало б пропустити розбіжність у
    /// третьому знаку як «нижче подання».
    /// </remarks>
    public static int? ScaleOf(string? displayFormat)
    {
        if (string.IsNullOrWhiteSpace(displayFormat))
        {
            return null;
        }

        var digits = 0;
        var multiplier = 0;
        var afterPoint = false;
        var sawPlaceholder = false;

        for (var i = 0; i < displayFormat.Length; i++)
        {
            var c = displayFormat[i];

            switch (c)
            {
                // Секції «додатне;від'ємне;нуль» показують те саме число
                // однаково; читається перша, решта — те саме іншим кольором.
                case ';':
                    i = displayFormat.Length;
                    break;

                case '\\':
                    i++;
                    break;

                case '"':
                case '\'':
                    // Літерал у лапках — текст, а не розряди: `0" т"` подає
                    // нуль знаків, а не один (`т` тут не плейсхолдер).
                    var close = displayFormat.IndexOf(c, i + 1);
                    i = close < 0 ? displayFormat.Length : close;
                    break;

                // ⛔ Науковий запис не задає сталої кількості знаків після
                // коми: `0.00E+00` подає різну точність для 1e3 і 1e-9.
                // Округлювати за ним означало б вигадати критерій.
                case 'E' or 'e' when i + 1 < displayFormat.Length
                                     && displayFormat[i + 1] is '+' or '-' or '0':
                    return null;

                case '%':
                    multiplier += 2;
                    break;

                case '‰':
                    multiplier += 3;
                    break;

                case '.':
                    afterPoint = true;
                    break;

                case ',':
                    // ⚠ Кома одразу перед кінцем секції або перед комою —
                    // це масштабування на тисячі, а не роздільник розрядів.
                    // Ми його не рахуємо і тому чесно кажемо «не знаю».
                    if (sawPlaceholder && !afterPoint && IsScalingComma(displayFormat, i))
                    {
                        return null;
                    }

                    break;

                case '0' or '#':
                    sawPlaceholder = true;
                    if (afterPoint)
                    {
                        digits++;
                    }

                    break;

                default:
                    break;
            }
        }

        return sawPlaceholder ? digits + multiplier : null;
    }

    /// <summary>Чи є кома в позиції масштабуванням на тисячі.</summary>
    /// <param name="format">Формат.</param>
    /// <param name="index">Позиція коми.</param>
    private static bool IsScalingComma(string format, int index)
    {
        for (var i = index + 1; i < format.Length; i++)
        {
            if (format[i] == ',')
            {
                continue;
            }

            return format[i] is not ('0' or '#' or '.');
        }

        return true;
    }

    /// <summary>Округлення до подання; без відомої розрядності — без змін.</summary>
    /// <param name="value">Число.</param>
    /// <param name="scale">Знаків після коми.</param>
    /// <remarks>
    /// ⛔ Правило — <see cref="MidpointRounding.AwayFromZero"/> (<c>ФВ-9.16a</c>):
    /// це округлення **подання**, тобто те, як число бачить людина у формі, а
    /// не арифметика режиму. Банківське дало б інші числа у вже поданих формах.
    ///
    /// ⚠ Невідома розрядність не підміняється нулем і не ховається: числа
    /// порівнюються як є, і розбіжність у сімнадцятому знаку піде в
    /// «блокує cutover». Це навмисно гучно — колонка без формату подання має
    /// бути помічена, а не мовчки звірена абияк.
    /// </remarks>
    private static decimal? Round(decimal? value, int? scale)
    {
        if (value is null)
        {
            return null;
        }

        if (scale is null)
        {
            return value;
        }

        // `decimal.Round` бере не більш ніж 28 знаків; більше не буває й у
        // форматі, але межа тут явна, щоб формат-помилка не кидала виняток.
        var digits = Math.Clamp(scale.Value, 0, 28);

        return decimal.Round(value.Value, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>Рядок звіту — читабельно, з обома числами.</summary>
    /// <param name="verdict">Вердикт.</param>
    /// <returns>Опис для звіту звірки.</returns>
    public static string Describe(CutoverVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var culture = CultureInfo.InvariantCulture;

        return verdict.Category switch
        {
            CutoverCategory.Match => "збіг",
            CutoverCategory.LegacySilent =>
                $"чинна система не дала результату; наш рушій дав {verdict.Actual?.ToString(culture)}",
            CutoverCategory.ExplainCaseByCase =>
                $"у поданні збігається ({verdict.RoundedActual?.ToString(culture)}), "
                + $"до округлення {verdict.Legacy?.ToString(culture)} проти "
                + $"{verdict.Actual?.ToString(culture)} — наслідок float-джерела",
            _ when verdict.Actual is null =>
                $"чинна система дала {verdict.Legacy?.ToString(culture)}, наш рушій — нічого",
            _ =>
                $"{verdict.RoundedLegacy?.ToString(culture)} проти "
                + $"{verdict.RoundedActual?.ToString(culture)} у поданні колонки",
        };
    }
}
