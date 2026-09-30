using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Значення виразу. Помилки — **значення**, а не винятки: одна зіпсована
/// комірка не має валити перерахунок усієї таблиці (02b §6.4).
/// </summary>
public readonly record struct ExpressionValue
{
    private ExpressionValue(ExpressionValueType type, object? value, string? errorCode)
    {
        Type = type;
        Value = value;
        ErrorCode = errorCode;
    }

    public ExpressionValueType Type { get; }
    public object? Value { get; }

    /// <summary>Код помилки (<c>#DIV/0</c>, <c>#REF</c>, <c>#VALUE</c>, <c>#UNIT</c>, <c>#CYCLE</c>).</summary>
    public string? ErrorCode { get; }

    public bool IsNull => Type == ExpressionValueType.Null;
    public bool IsError => Type == ExpressionValueType.Error;

    /// <summary>Порожнє значення.</summary>
    public static ExpressionValue Null { get; } = new(ExpressionValueType.Null, null, null);

    /// <summary>Число в <see cref="decimal"/> — режим <c>Strict</c>.</summary>
    /// <param name="v">Значення.</param>
    public static ExpressionValue Number(decimal v) => new(ExpressionValueType.Number, v, null);

    /// <summary>
    /// Число в <see cref="double"/> — режим <c>Legacy</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Назва навмисно НЕ перевантажує <c>Number</c>: цілий літерал
    /// приводиться і до <c>decimal</c>, і до <c>double</c>, тож перевантаження
    /// давало б неоднозначність у кожному другому виклику. Але виграш не лише
    /// технічний — у місці виклику видно, який режим породив значення.
    /// </remarks>
    /// <remarks>
    /// ⛔ Подвійна точність тут не недбалість, а **умова сумісності**. Чинна
    /// система рахує діалект методологій у NCalc, а той — у <c>double</c>
    /// (виміряно: <c>docs/legacy-ncalc-1.3.8.md</c>). Уже подані регулятору
    /// форми містять саме ці числа, і <c>ER-C-11</c> («зміна останніх знаків у
    /// вже поданих формах неприпустима») виконується не тим, що ми рахуємо
    /// краще, а тим, що рахуємо **так само**.
    ///
    /// ⚠ Значення живе <c>double</c> протягом усього обчислення формули і
    /// звужується до <c>decimal(34,16)</c> лише при збереженні. Проміжне
    /// звуження дало б інші числа — саме те, чого <c>Legacy</c> має не робити.
    ///
    /// ⛔ <c>D-30</c> не порушено: він про **тип колонки**, а не про арифметику.
    /// </remarks>
    /// <param name="v">Значення.</param>
    public static ExpressionValue LegacyNumber(double v) => new(ExpressionValueType.Number, v, null);
    public static ExpressionValue Text(string v) => new(ExpressionValueType.Text, v, null);
    public static ExpressionValue Boolean(bool v) => new(ExpressionValueType.Boolean, v, null);
    public static ExpressionValue Date(DateTime v) => new(ExpressionValueType.Date, v, null);

    /// <summary>Помилка обчислення.</summary>
    public static ExpressionValue Error(string code) => new(ExpressionValueType.Error, null, code);

    /// <summary>
    /// Чи несе значення подвійну точність, а не <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Потрібно там, де різниця видима ззовні: у трейсі, при збереженні і
    /// при масковці <c>NaN</c>/нескінченності (<c>MaskedZero</c>). Логіка
    /// обчислення про подання не питає — за неї відповідає
    /// <see cref="IEvaluationArithmetic"/>.
    /// </remarks>
    public bool IsDouble => Value is double;

    /// <summary>
    /// Значення як <see cref="decimal"/>; <c>null</c>, якщо це не число.
    /// </summary>
    /// <remarks>
    /// ⛔ Для <c>Legacy</c>-значення це **звуження**, і кликати його всередині
    /// обчислення не можна: втрачені знаки вже не повернуться, а різниця
    /// вилізе в шостому знаку звірки. Місце виклику одне — межа збереження.
    ///
    /// ⚠ <c>NaN</c> і нескінченність у <c>decimal</c> не подаються взагалі,
    /// тому тут вони дають <c>null</c>: маскувати їх у нуль — робота того, хто
    /// зберігає, і робить він це з записом причини в трейс.
    /// </remarks>
    public decimal? AsNumber()
    {
        if (Type != ExpressionValueType.Number)
        {
            return null;
        }

        if (Value is double d)
        {
            return double.IsFinite(d) ? Narrow(d) : null;
        }

        return (decimal)Value!;
    }

    /// <summary>
    /// Звуження скінченного <see cref="double"/>; <c>null</c>, якщо модуль
    /// більший за межу <see cref="decimal"/> (≈ 7.9·10²⁸).
    /// </summary>
    /// <remarks>
    /// ⛔ Аудит A2: у <c>Legacy</c> <c>Pow(10, 30)</c> — скінченне число, як і
    /// в NCalc 1.3.8, а <c>(decimal)1e30</c> КИДАЄ <see cref="OverflowException"/>.
    /// Звідси виняток валив увесь прогін прив'язки (<c>GenericCalculationModule</c>
    /// кличе цей метод для трейсу кожного кроку).
    ///
    /// ⚠ <c>null</c>, як для <c>NaN</c>/<c>±∞</c> вище: значення лишається
    /// <see cref="double"/> і далі в обчисленні (<c>1e30 / 1e20</c> дає
    /// <c>1e10</c>, як у NCalc), а в <c>decimal</c> його подати неможливо.
    /// Маскувати в нуль, як нескінченність, НЕ можна: чинна система скінченне
    /// число не обнуляє, і нуль тут був би вигаданим результатом.
    ///
    /// ⚠ Межа перевіряється ловлею, а не порівнянням: <c>(double)decimal.MaxValue</c>
    /// округлюється ВГОРУ до 2⁹⁶ і сам уже не звужується назад, а конверсія
    /// округлює мантису (документовано — до 15 значущих цифр), тож точну межу
    /// числом не записати. Виняток тут рідкісний і дешевший за помилку на межі.
    /// </remarks>
    private static decimal? Narrow(double d)
    {
        try
        {
            return (decimal)d;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Значення як <see cref="double"/>; <c>null</c>, якщо це не число.
    /// </summary>
    /// <remarks>
    /// ⚠ Розширення <c>decimal</c> → <c>double</c> втрачає точність за межею
    /// 15–17 значущих цифр. У <c>Legacy</c> це нешкідливо: там значення й так
    /// прийшло з <c>double</c>. У <c>Strict</c> цей метод не кличеться.
    /// </remarks>
    public double? AsDouble() => Type == ExpressionValueType.Number
        ? Value is double d ? d : (double)(decimal)Value!
        : null;
}
