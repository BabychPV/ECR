using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Тринадцять функцій, які діалект <c>Methodology</c> додає до одинадцяти
/// спільних із <see cref="TemplateFunctions"/> — разом 24 (<c>02b</c> §8).
/// </summary>
/// <remarks>
/// ⚠ <c>CONVERT</c> тут навмисно немає: вона живе окремо в
/// <see cref="ConvertFunction"/>, бо це **єдиний** спосіб змінити одиницю
/// (<c>02b</c> §9) і вона потребує доступу до <c>uom.*</c>, тоді як решта
/// функцій чиста.
///
/// ⛔ Функцій поточного часу (<c>NOW</c>, <c>TODAY</c>), випадковості,
/// введення-виведення і звернень до БД у діалекті немає **в обох діалектах**.
/// Час береться лише з календарного контексту (<c>02b</c> §10) — інакше
/// результат перерахунку залежав би від дня, коли його запустили, і звірка з
/// еталоном стала б неможливою.
/// </remarks>
public static class MethodologyFunctions
{
    /// <summary>Степінь; те саме, що оператор <c>^</c>.</summary>
    public static ExpressionValue Power(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: два аргументи; обчислювати в decimal. ⚠ Math.Pow повертає double — " +
            "використовувати його заборонено (D-30): цілі показники підносити множенням, " +
            "дробові — через розширену точність, інакше звірка з еталоном розійдеться.");

    /// <summary>Квадратний корінь; від'ємний аргумент → <c>#VALUE</c>.</summary>
    public static ExpressionValue Sqrt(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: від'ємний аргумент — це ЗНАЧЕННЯ #VALUE, а не виняток (02b §6.4).");

    /// <summary>Експонента.</summary>
    public static ExpressionValue Exp(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: обчислювати в decimal; переповнення → #VALUE.");

    /// <summary>Натуральний логарифм; аргумент ≤ 0 → <c>#VALUE</c>.</summary>
    public static ExpressionValue Ln(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: аргумент ≤ 0 → #VALUE як значення.");

    /// <summary>Десятковий логарифм.</summary>
    public static ExpressionValue Log10(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: аргумент ≤ 0 → #VALUE як значення.");

    /// <summary>Округлення вгору до кратного <c>significance</c>.</summary>
    public static ExpressionValue Ceiling(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: другий аргумент необов'язковий (типово 1). Округлення до кратного, " +
            "а не до розряду — це різні операції.");

    /// <summary>Округлення вниз до кратного <c>significance</c>.</summary>
    public static ExpressionValue Floor(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: другий аргумент необов'язковий (типово 1).");

    /// <summary>Відкидання дробової частини — <b>не</b> округлення.</summary>
    public static ExpressionValue Trunc(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: відкидати, а не округлювати: TRUNC(-1.7) = -1, тоді як ROUND дав би -2. " +
            "Плутанина тут дає розбіжність у знаку на від'ємних значеннях.");

    /// <summary>Остача; знак результату — як у діленого.</summary>
    public static ExpressionValue Mod(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: знак результату — як у ДІЛЕНОГО (як у C# %), а не як у дільника (як в Excel). " +
            "Ділення на нуль → #DIV/0 як значення.");

    /// <summary>Перше не-<c>null</c> значення.</summary>
    public static ExpressionValue Coalesce(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: перше не-null; усі null → null. ⚠ Помилка (#DIV/0 тощо) НЕ є null " +
            "і повертається як є — перехоплює її лише IFERROR.");

    /// <summary>Вибір за збігом; без збігу і без <c>default</c> → <c>null</c>.</summary>
    public static ExpressionValue Switch(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: SWITCH(value, case1, result1, …, default?). Парність аргументів після value " +
            "визначає, чи є default. Без збігу і без default → null, а не 0.");

    /// <summary>Значення для конкретної речовини в контексті методології.</summary>
    public static ExpressionValue Substance(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: узяти код речовини з аргументу і резолвити його в поточному контексті " +
            "обчислення (IEvaluationContext). Речовина, якої немає серед calc.MethodologySubstance " +
            "цієї версії, — помилка конфігурації, а не null.");
}
