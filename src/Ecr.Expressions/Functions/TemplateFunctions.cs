using Ecr.Expressions.Ast;
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
///
/// ⚠ Скрізь у цьому файлі діє правило «<c>null</c> в агрегатах **поглинається**»
/// (02b §6.1) — на відміну від бінарних операторів, де він **поширюється**
/// (§6.2). Плутати їх не можна: саме тут народжуються розбіжності зі старою
/// системою. Помилка ж поширюється в обох випадках: зіпсована комірка не має
/// тихо випадати із суми.
/// </remarks>
public static class TemplateFunctions
{
    /// <summary>Сума; <c>null</c> ігноруються; порожня множина → <c>0</c>.</summary>
    public static ExpressionValue Sum(IReadOnlyList<ExpressionValue> args)
    {
        var numbers = Numbers(args, out var failure);
        if (failure is { } error)
        {
            return error;
        }

        var total = 0m;
        foreach (var n in numbers)
        {
            total += n;
        }

        return ExpressionValue.Number(total);
    }

    /// <summary>Середнє не-<c>null</c>; порожня множина → <c>null</c> (а не <c>0</c>).</summary>
    public static ExpressionValue Average(IReadOnlyList<ExpressionValue> args)
    {
        var numbers = Numbers(args, out var failure);
        if (failure is { } error)
        {
            return error;
        }

        // ⚠ null не входить ані в суму, ані в ДІЛЬНИК. Порожня множина дає
        // null, а не 0: «середнє ні з чого» — це не нуль, і показати нуль
        // означало б збрехати у звіті.
        if (numbers.Count == 0)
        {
            return ExpressionValue.Null;
        }

        var total = 0m;
        foreach (var n in numbers)
        {
            total += n;
        }

        return ExpressionValue.Number(total / numbers.Count);
    }

    /// <summary>Мінімум не-<c>null</c>; порожня множина → <c>null</c>.</summary>
    public static ExpressionValue Min(IReadOnlyList<ExpressionValue> args)
    {
        var numbers = Numbers(args, out var failure);
        if (failure is { } error)
        {
            return error;
        }

        return numbers.Count == 0 ? ExpressionValue.Null : ExpressionValue.Number(numbers.Min());
    }

    /// <summary>Максимум не-<c>null</c>; порожня множина → <c>null</c>.</summary>
    public static ExpressionValue Max(IReadOnlyList<ExpressionValue> args)
    {
        var numbers = Numbers(args, out var failure);
        if (failure is { } error)
        {
            return error;
        }

        return numbers.Count == 0 ? ExpressionValue.Null : ExpressionValue.Number(numbers.Max());
    }

    /// <summary>Кількість не-<c>null</c> значень.</summary>
    public static ExpressionValue Count(IReadOnlyList<ExpressionValue> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var count = 0;
        foreach (var arg in args)
        {
            // Помилка — не значення для підрахунку: комірка з #DIV/0 не
            // «заповнена», вона зіпсована.
            if (arg.Type is not ExpressionValueType.Null and not ExpressionValueType.Error)
            {
                count++;
            }
        }

        return ExpressionValue.Number(count);
    }

    /// <summary>
    /// Округлення. **<c>MidpointRounding.AwayFromZero</c>** — саме так рахує чинна
    /// система; банківське округлення дало б інші числа у звіті.
    /// </summary>
    public static ExpressionValue Round(ExpressionValue value, ExpressionValue digits)
    {
        if (value.IsError)
        {
            return value;
        }

        if (digits.IsError)
        {
            return digits;
        }

        if (value.IsNull || digits.IsNull)
        {
            return ExpressionValue.Null;
        }

        if (value.AsNumber() is not { } number || digits.AsNumber() is not { } places)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var scale = (int)decimal.Truncate(places);
        if (scale is < 0 or > 28)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // ⚠ AwayFromZero, а не ToEven: ROUND(0.5, 0) = 1, а банківське дало б 0.
        // Різниця видна саме на .5 і саме там, де рахуються тонни викидів.
        return ExpressionValue.Number(Math.Round(number, scale, MidpointRounding.AwayFromZero));
    }

    /// <summary>Модуль числа.</summary>
    public static ExpressionValue Abs(ExpressionValue value)
    {
        if (value.IsError)
        {
            return value;
        }

        if (value.IsNull)
        {
            return ExpressionValue.Null;
        }

        return value.AsNumber() is { } number
            ? ExpressionValue.Number(Math.Abs(number))
            : ExpressionValue.Error(ExpressionErrors.BadValue);
    }

    /// <summary>Добуток не-<c>null</c>; порожня множина → <c>1</c>.</summary>
    public static ExpressionValue Product(IReadOnlyList<ExpressionValue> args)
    {
        var numbers = Numbers(args, out var failure);
        if (failure is { } error)
        {
            return error;
        }

        // Порожня множина → 1, а не 0: одиниця — нейтральний елемент множення,
        // і нуль тут занулив би все, що на цей добуток помножать далі.
        var product = 1m;
        foreach (var n in numbers)
        {
            product *= n;
        }

        return ExpressionValue.Number(product);
    }

    /// <summary>Вибір гілки за умовою.</summary>
    /// <remarks>
    /// Тип умови перевіряє <c>TypeChecker</c> ПРИ ПУБЛІКАЦІЇ; тут лишається
    /// тільки вибір. Нечислова умова в рантаймі означає, що перевірку обійшли,
    /// і це <c>#VALUE</c>, а не мовчазна гілка.
    /// </remarks>
    public static ExpressionValue If(ExpressionValue condition, ExpressionValue then, ExpressionValue otherwise)
    {
        if (condition.IsError)
        {
            return condition;
        }

        if (condition.IsNull)
        {
            return ExpressionValue.Null;
        }

        if (condition.Type != ExpressionValueType.Boolean)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return (bool)condition.Value! ? then : otherwise;
    }

    /// <summary>Перехоплює **лише** помилки, не <c>null</c>.</summary>
    public static ExpressionValue IfError(ExpressionValue value, ExpressionValue fallback)
        // ⚠ null НЕ перехоплюється: «не заповнено» — не помилка, і підміна
        // порожнечі запасним значенням тихо дописала б у звіт число, якого
        // ніхто не вводив (02c E08).
        => value.IsError ? fallback : value;

    /// <summary>Сума елементів, для яких умова істинна.</summary>
    /// <remarks>
    /// Умова передається **паралельним діапазоном**, а не рядковим критерієм у
    /// стилі Excel: критерій-рядок (<c>"&gt;100"</c>) — це друга міні-мова
    /// всередині мови, а вона тут свідомо не заводиться (02b §4.2).
    /// </remarks>
    /// <param name="range">Значення, які перевіряються і, за замовчуванням, підсумовуються.</param>
    /// <param name="conditions">Умови, паралельні <paramref name="range"/>.</param>
    /// <param name="sumRange">Що саме підсумовувати; <c>null</c> — сам <paramref name="range"/>.</param>
    public static ExpressionValue SumIf(IReadOnlyList<ExpressionValue> range,
                                        IReadOnlyList<ExpressionValue> conditions,
                                        IReadOnlyList<ExpressionValue>? sumRange)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(conditions);

        var source = sumRange ?? range;
        var total = 0m;

        for (var i = 0; i < conditions.Count && i < source.Count; i++)
        {
            var condition = conditions[i];
            if (condition.IsError)
            {
                return condition;
            }

            if (condition.Type != ExpressionValueType.Boolean || !(bool)condition.Value!)
            {
                continue;
            }

            var value = source[i];
            if (value.IsError)
            {
                return value;
            }

            if (value.AsNumber() is { } number)
            {
                total += number;
            }
        }

        return ExpressionValue.Number(total);
    }

    /// <summary>
    /// Числа з аргументів: <c>null</c> поглинаються, помилка зупиняє агрегат.
    /// </summary>
    private static List<decimal> Numbers(IReadOnlyList<ExpressionValue> args, out ExpressionValue? failure)
    {
        ArgumentNullException.ThrowIfNull(args);

        var numbers = new List<decimal>(args.Count);
        foreach (var arg in args)
        {
            if (arg.IsError)
            {
                failure = arg;
                return numbers;
            }

            if (arg.IsNull)
            {
                continue;
            }

            if (arg.AsNumber() is not { } number)
            {
                failure = ExpressionValue.Error(ExpressionErrors.BadValue);
                return numbers;
            }

            numbers.Add(number);
        }

        failure = null;
        return numbers;
    }
}
