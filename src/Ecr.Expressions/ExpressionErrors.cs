namespace Ecr.Expressions;

/// <summary>
/// Коди помилок, які повертає рушій виразів.
/// </summary>
/// <remarks>
/// ⚠ Значення продубльовані з каталогу <c>Ecr.Api.Errors.ErrorCodes</c>
/// навмисно: <c>Ecr.Expressions</c> не має права посилатися на шар API
/// (архітектурне правило «залежності лише всередину»). За збігом стежить
/// перевірка унікальності кодів у <c>ContractIntegrityTests</c>.
/// </remarks>
public static class ExpressionErrors
{
    /// <summary>Синтаксис, невідома функція, невірна кількість аргументів.</summary>
    public const string Syntax = "ECR-TMPL-0422";

    /// <summary>Цикл у графі залежностей формул.</summary>
    public const string Cycle = "ECR-TMPL-4221";

    /// <summary>Посилання не резолвиться або тип несумісний.</summary>
    public const string Unresolved = "ECR-TMPL-4222";

    /// <summary>Несумісні одиниці без явного <c>CONVERT</c>.</summary>
    public const string UnitMismatch = "ECR-TMPL-4223";

    /// <summary>
    /// Правила однієї області дії з різними рівнями (<c>ФВ-5.10</c>).
    /// </summary>
    /// <remarks>
    /// Комірковий <c>Error</c> блокує запис, а <c>Warning</c> — ні. Дві
    /// такі дії на ту саму комірку означають, що оператор бачить пораду,
    /// якої не може виконати: зберегти рядок однаково не дадуть.
    /// </remarks>
    public const string RuleConflict = "ECR-TMPL-4224";

    /// <summary>
    /// Обов'язкова колонка без правила і без формули (<c>ФВ-5.11</c>).
    /// </summary>
    /// <remarks>
    /// Порожню комірку, якої НЕ ТОРКАЛИСЯ, структурна перевірка не бачить:
    /// вона спрацьовує на запису. Тому «обов'язкова» без жодного правила —
    /// це обіцянка без виконавця: подання пройде з незаповненим полем.
    /// </remarks>
    public const string RequiredNotCovered = "ECR-TMPL-4225";

    /// <summary>
    /// <c>^</c> у діалекті методологій (директива №05 §4).
    /// </summary>
    /// <remarks>
    /// ⛔ Окремий код, а не <see cref="Syntax"/>, бо це не описка. У NCalc 1.3.8
    /// <c>^</c> — **побітовий XOR**: <c>2^3</c> там дорівнює 1, а не 8 (замір,
    /// `docs/legacy-ncalc-1.3.8.md`). Формула зі степенем, написана через
    /// <c>^</c>, тому не «не компілюється» — вона мовчки рахує інше число, і
    /// повідомлення мусить пояснити саме це, а не «неочікувану лексему».
    ///
    /// ⚠ Префікс <c>CALC</c>, а не <c>TMPL</c>: помилка належить публікації
    /// МЕТОДОЛОГІЇ, як і решта родини <c>ECR-CALC-04xx</c>.
    /// </remarks>
    public const string CaretNotPower = "ECR-CALC-0431";

    // ——— помилки-ЗНАЧЕННЯ (02b §6.4): вони живуть у комірці, а не в діагностиці ———

    /// <summary>Ділення на нуль або на <c>null</c>.</summary>
    public const string DivideByZero = "#DIV/0";

    /// <summary>Посилання не резолвиться в рантаймі (рядок видалено).</summary>
    public const string BadReference = "#REF";

    /// <summary>Несумісні типи, які не вдалося відсіяти при публікації.</summary>
    public const string BadValue = "#VALUE";

    /// <summary>Несумісні одиниці в рантаймі.</summary>
    public const string BadUnit = "#UNIT";

    /// <summary>Цикл, виявлений у рантаймі — аварійний випадок.</summary>
    public const string RuntimeCycle = "#CYCLE";
}
