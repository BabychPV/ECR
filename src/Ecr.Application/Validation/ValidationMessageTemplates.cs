using System.Globalization;
using Ecr.Application.Localization;

namespace Ecr.Application.Validation;

/// <summary>
/// Шаблони повідомлень двигуна валідації (Check, структурні порушення, зламане правило) за ключем каталогу:
/// ключ + підстановки зберігаються в <see cref="ValidationMessage"/>, текст мовою читача збирається НА ЧИТАННІ
/// (T2-07 / T3-03 / T4-06).
/// </summary>
/// <remarks>
/// ⚠ Два місця правди, з навмисною роллю кожного: рядки <c>sys_ecr.UiString</c> (секція <c>COLL:an42vm</c>
/// у <c>09-seed.sql</c>) — те, що бачить читач і що може поправити адміністратор; шаблони тут — текст ПРИ
/// СТВОРЕННІ повідомлення (синхронні <c>ValidateCell</c>/<c>ValidateScope</c> не мають каталогу) і запасний
/// текст читання, коли ключа в каталозі немає. Збіг двох мов-наборів тримає архітектурний сторож
/// <c>ValidationTemplateSeedTests</c>.
/// </remarks>
public static class ValidationMessageTemplates
{
    /// <summary>Check: значення колонок не сходяться в межах допуску.</summary>
    public const string CheckMismatch = "validation.check.mismatch";

    /// <summary>Check без ключів при приймачі не з одного рядка: нічого не порівняно (A2-03, інформація).</summary>
    public const string CheckNoKeys = "validation.check.noKeys";

    /// <summary>
    /// Check з ідентифікацією рядка джерела (A2-04): кілька рядків джерела проти одного рядка приймача давали
    /// однакові тексти. <see cref="CheckMismatch"/> лишається для збережених раніше результатів (їх підстановки
    /// не мають <c>sourceRow</c>).
    /// </summary>
    public const string CheckMismatchRow = "validation.check.mismatchRow";

    /// <summary>Обов'язкова колонка без значення.</summary>
    public const string ColumnRequired = "validation.column.required";

    /// <summary>Більше десяткових знаків, ніж дозволяє колонка.</summary>
    public const string ColumnScale = "validation.column.scale";

    /// <summary>Значення не вміщується в точність колонки.</summary>
    public const string ColumnPrecision = "validation.column.precision";

    /// <summary>Текст довший за стовпець сховища (<c>ColumnDef.MaxStringLength</c>).</summary>
    public const string ColumnMaxLength = "validation.column.maxLength";

    /// <summary>Вираз правила не розбирається.</summary>
    public const string RuleParseError = "validation.rule.parseError";

    /// <summary>Правило не дало логічної відповіді.</summary>
    public const string RuleNotLogical = "validation.rule.notLogical";

    /// <summary>Правило не обчислилося: формула завелика для одного обчислення (бюджет кроків або глибини).</summary>
    public const string RuleBudget = "validation.rule.budget";

    /// <summary>Rule (h4): the row's permit does not cover months that hold data.</summary>
    public const string PermitMonthsNotCovered = "validation.permit.monthsNotCovered";

    /// <summary>Rule (h4): the header (Contract tab) permit does not cover months the document holds data in.</summary>
    public const string PermitHeaderMonthsNotCovered = "validation.permit.headerMonthsNotCovered";

    /// <summary>Rule (h), human 08.11: the permit covers a month with data only partly — a non-blocking note.</summary>
    public const string PermitPartialMonth = "validation.permit.partialMonth";

    /// <summary>
    /// Human 08.11 («Попередження», F13-p210): the row's permit does not cover months holding only CALCULATED values — a
    /// non-blocking Warning; the fix is the permit, not the values (calculated values cannot be cleared).
    /// </summary>
    public const string PermitCalculatedNotCovering = "validation.permit.calculatedNotCovering";

    /// <summary>The same as <see cref="PermitCalculatedNotCovering"/> when exactly one month is named («that month»).</summary>
    public const string PermitCalculatedNotCoveringOne = "validation.permit.calculatedNotCoveringOne";

    private static readonly Dictionary<string, (string En, string Ru, string Kz)> Table = new(StringComparer.Ordinal)
    {
        [PermitCalculatedNotCovering] = (
            "Permit {permit} (column \"{column}\") is valid {validity} and does not cover the months with calculated values: {months}. Saving and submitting are allowed. Pick a permit valid in those months or extend the permit's validity.",
            "Разрешение {permit} (колонка «{column}») действует {validity} и не покрывает месяцы с вычисленными значениями: {months}. Сохранение и подача разрешены. Выберите разрешение, действующее в эти месяцы, или продлите срок действия разрешения.",
            "{permit} рұқсаты («{column}» бағаны) {validity} аралығында жарамды және есептелген мәндері бар айларды қамтымайды: {months}. Сақтауға және тапсыруға болады. Осы айларда жарамды рұқсатты таңдаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз."),
        [PermitCalculatedNotCoveringOne] = (
            "Permit {permit} (column \"{column}\") is valid {validity} and does not cover the month with calculated values: {months}. Saving and submitting are allowed. Pick a permit valid in that month or extend the permit's validity.",
            "Разрешение {permit} (колонка «{column}») действует {validity} и не покрывает месяц с вычисленными значениями: {months}. Сохранение и подача разрешены. Выберите разрешение, действующее в этом месяце, или продлите срок действия разрешения.",
            "{permit} рұқсаты («{column}» бағаны) {validity} аралығында жарамды және есептелген мәндері бар айды қамтымайды: {months}. Сақтауға және тапсыруға болады. Осы айда жарамды рұқсатты таңдаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз."),
        [PermitPartialMonth] = (
            "Permit {permit} (column \"{column}\") is valid only {days} in month {month}: the values of that month are accepted, but the permit covers those days only.",
            "Разрешение {permit} (колонка «{column}») действует только {days} в месяце {month}: значения этого месяца приняты, но разрешение покрывает только эти дни.",
            "{permit} рұқсаты («{column}» бағаны) {month} айында тек {days} аралығында жарамды: осы айдың мәндері қабылданды, бірақ рұқсат тек осы күндерді қамтиды."),
        [PermitMonthsNotCovered] = (
            "Permit {permit} (column \"{column}\") is valid {validity} and does not cover the months with data: {months}. Pick a permit valid in those months, clear the values in those months, or extend the permit's validity.",
            "Разрешение {permit} (колонка «{column}») действует {validity} и не покрывает месяцы с данными: {months}. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.",
            "{permit} рұқсаты («{column}» бағаны) {validity} аралығында жарамды және деректері бар айларды қамтымайды: {months}. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз."),
        [PermitHeaderMonthsNotCovered] = (
            "Header field \"{column}\": permit {permit} is valid {validity} and does not cover the months the document holds data in: {months}. Pick a permit valid in those months, clear the values in those months, or extend the permit's validity.",
            "Поле шапки «{column}»: разрешение {permit} действует {validity} и не покрывает месяцы, за которые в документе есть данные: {months}. Выберите разрешение, действующее в эти месяцы, очистите значения за эти месяцы или продлите срок действия разрешения.",
            "«{column}» тақырып өрісі: {permit} рұқсаты {validity} аралығында жарамды және құжатта деректері бар айларды қамтымайды: {months}. Осы айларда жарамды рұқсатты таңдаңыз, осы айлардағы мәндерді тазалаңыз немесе рұқсаттың жарамдылық мерзімін ұзартыңыз."),
        [CheckMismatch] = (
            "Check: {left} = {leftValue} does not match {right} = {rightValue}: deviation {deviation}, allowed {allowed} ({kind}).",
            "Сверка: {left} = {leftValue} не сходится с {right} = {rightValue}: отклонение {deviation}, допустимо {allowed} ({kind}).",
            "Салыстыру: {left} = {leftValue} мәні {right} = {rightValue} мәніне сәйкес келмейді: ауытқу {deviation}, рұқсат етілгені {allowed} ({kind})."),
        [CheckNoKeys] = (
            "A Check without keys compares only when the receiving table has exactly one row; it has {targetRows}, so nothing was compared. Unknown row matching fields are ignored.",
            "Сверка без ключей сравнивает только при ровно одной строке таблицы-приёмника; в ней строк: {targetRows}, поэтому ничего не сравнивалось. Неизвестные поля сопоставления строк игнорируются.",
            "Кілтсіз салыстыру қабылдағыш кестеде дәл бір жол болғанда ғана жұмыс істейді; онда {targetRows} жол бар, сондықтан ештеңе салыстырылмады. Жолдарды сәйкестендірудің белгісіз өрістері еленбейді."),
        [CheckMismatchRow] = (
            "Check: {left} = {leftValue} (source row {sourceRow}) does not match {right} = {rightValue}: deviation {deviation}, allowed {allowed} ({kind}).",
            "Сверка: {left} = {leftValue} (строка источника {sourceRow}) не сходится с {right} = {rightValue}: отклонение {deviation}, допустимо {allowed} ({kind}).",
            "Салыстыру: {left} = {leftValue} (дереккөз жолы {sourceRow}) мәні {right} = {rightValue} мәніне сәйкес келмейді: ауытқу {deviation}, рұқсат етілгені {allowed} ({kind})."),
        [ColumnRequired] = (
            "Column \"{column}\" is required.",
            "Колонка «{column}» обязательна.",
            "«{column}» бағаны міндетті."),
        [ColumnScale] = (
            "Column \"{column}\" allows at most {scale} decimal places.",
            "Колонка «{column}» допускает не более {scale} знаков после запятой.",
            "«{column}» бағанында үтірден кейін {scale} таңбадан артық болмауы керек."),
        [ColumnPrecision] = (
            "The value does not fit the precision of column \"{column}\" ({precision} digits).",
            "Значение не помещается в точность колонки «{column}» ({precision} цифр).",
            "Мән «{column}» бағанының дәлдігіне ({precision} сан) сыймайды."),
        [ColumnMaxLength] = (
            "The text in column \"{column}\" is longer than {maxLength} characters ({actualLength}).",
            "Текст в колонке «{column}» длиннее {maxLength} символов ({actualLength}).",
            "«{column}» бағанындағы мәтін {maxLength} таңбадан ұзын ({actualLength})."),
        [RuleParseError] = (
            "Rule '{rule}' does not parse: {detail}",
            "Правило '{rule}' не разбирается: {detail}",
            "'{rule}' ережесі талдана алмайды: {detail}"),
        [RuleNotLogical] = (
            "Rule '{rule}' did not return a logical answer: {reason}",
            "Правило '{rule}' не дало логического ответа: {reason}",
            "'{rule}' ережесі логикалық жауап бермеді: {reason}"),
        [RuleBudget] = (
            "Rule '{rule}' could not be evaluated: the formula is too large for one calculation (more than 20,000 steps or 96 nesting levels). Split it into several calculated columns.",
            "Правило '{rule}' не удалось вычислить: формула слишком велика для одного расчёта (больше 20 000 шагов или 96 уровней вложенности). Разбейте её на несколько вычисляемых колонок.",
            "'{rule}' ережесін есептеу мүмкін болмады: формула бір есептеу үшін тым үлкен (20 000 қадамнан немесе 96 ену деңгейінен артық). Оны бірнеше есептелетін бағанға бөліңіз."),
    };

    /// <summary>Усі ключі шаблонів (для сторожа збігу з сідом).</summary>
    public static IReadOnlyCollection<string> Keys => Table.Keys;

    /// <summary>Шаблон мовою; невідома мова — англійська; невідомий ключ — <c>null</c>.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="language"><c>en</c>/<c>ru</c>/<c>kz</c>.</param>
    public static string? Template(string key, string? language)
        => Table.TryGetValue(key, out var t)
            ? language switch { "ru" => t.Ru, "kz" => t.Kz, _ => t.En }
            : null;

    /// <summary>Готовий текст ключа з підстановками мовою; невідомий ключ — сам ключ.</summary>
    /// <param name="key">Ключ.</param>
    /// <param name="language">Мова.</param>
    /// <param name="parameters">Підстановки.</param>
    public static string Render(string key, string? language, IReadOnlyDictionary<string, string> parameters)
        => UiStringResolver.Format(Template(key, language) ?? key, parameters);

    /// <summary>
    /// Повідомлення мовою читача: текст каталогу за <see cref="ValidationMessage.MessageKey"/> із підстановками
    /// <see cref="ValidationMessage.Params"/>; ключа немає в каталозі — запасний шаблон тут; повідомлення без
    /// ключа (старий результат, текст правила) чи без підстановок — без змін.
    /// </summary>
    /// <param name="message">Збережене повідомлення (вже пройшло маскування прихованого).</param>
    /// <param name="language">Мова читача.</param>
    /// <param name="catalog">Каталог мови читача; <c>null</c> — лише запасні шаблони.</param>
    public static ValidationMessage Localize(ValidationMessage message, string? language, Ports.UiStringCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.MessageKey is not { Length: > 0 } key || message.Params is null)
        {
            return message;
        }

        var template = catalog is not null && catalog.Strings.TryGetValue(key, out var fromCatalog) && !string.IsNullOrWhiteSpace(fromCatalog)
            ? fromCatalog
            : Template(key, language);

        return template is null
            ? message
            : message with { Message = UiStringResolver.Format(template, message.Params) };
    }

    /// <summary>Підстановка-число: інваріантна культура, як у каталозі помилок.</summary>
    /// <param name="value">Число.</param>
    public static string Num(decimal? value)
        => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? string.Empty;
}
