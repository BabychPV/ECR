using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Одинадцять функцій діалекту <c>Template</c> — рівно стільки, скільки
/// використовує чинний шаблон.
/// </summary>
/// <remarks>
/// <c>VLOOKUP</c> відсутній навмисно: усі 429 його входжень у чинному шаблоні —
/// звернення до довідників, які тут замінені посиланням на реєстр. Це не
/// спрощення, а усунення цілого класу помилок «діапазон з'їхав».
/// </remarks>
public static class TemplateFunctions
{
    /// <summary>Сума; <c>null</c> ігноруються; порожня множина → <c>0</c>.</summary>
    public static ExpressionValue Sum(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: підсумувати не-null числа в decimal; порожня множина → 0.");

    /// <summary>Середнє не-<c>null</c>; порожня множина → <c>null</c> (а не <c>0</c>).</summary>
    public static ExpressionValue Average(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: null не входять ані в суму, ані в дільник.");

    public static ExpressionValue Min(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: мінімум не-null; порожня → null.");

    public static ExpressionValue Max(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: максимум не-null; порожня → null.");

    public static ExpressionValue Count(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException(
            "TODO: підрахувати елементи, де Type != Null і не Error; помилки НЕ рахувати як значення.");

    /// <summary>
    /// Округлення. **`MidpointRounding.AwayFromZero`** — саме так рахує чинна
    /// система; банківське округлення дало б інші числа у звіті.
    /// </summary>
    public static ExpressionValue Round(ExpressionValue value, ExpressionValue digits)
        => throw new NotImplementedException(
            "TODO: Math.Round(decimal, int, MidpointRounding.AwayFromZero). " +
            "ROUND(2.5, 0) = 3, ROUND(-2.5, 0) = -3 (02c E12, E13).");

    public static ExpressionValue Abs(ExpressionValue value)
        => throw new NotImplementedException(
            "TODO: Math.Abs для decimal; null → null (поширення), помилка → та сама помилка.");

    /// <summary>Добуток не-<c>null</c>; порожня множина → <c>1</c>.</summary>
    public static ExpressionValue Product(IReadOnlyList<ExpressionValue> args)
        => throw new NotImplementedException("TODO: порожня множина → 1, не 0.");

    public static ExpressionValue If(ExpressionValue condition, ExpressionValue then, ExpressionValue otherwise)
        => throw new NotImplementedException(
            "TODO: condition має бути Boolean — інакше це помилка ПУБЛІКАЦІЇ, не рантайму; " +
            "тут лише вибір гілки. null-умова → null.");

    /// <summary>Перехоплює **лише** помилки, не <c>null</c>.</summary>
    public static ExpressionValue IfError(ExpressionValue value, ExpressionValue fallback)
        => throw new NotImplementedException("TODO: value.IsError → fallback; null → null (02c E8).");

    public static ExpressionValue SumIf(IReadOnlyList<ExpressionValue> range,
                                        IReadOnlyList<ExpressionValue> conditions,
                                        IReadOnlyList<ExpressionValue>? sumRange)
        => throw new NotImplementedException("TODO: підсумувати елементи, де умова TRUE.");
}
