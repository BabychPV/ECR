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
///
/// ⚠ Помилка тут завжди **значення**, ніколи виняток (<c>02b</c> §6.4): одна
/// зіпсована комірка не має валити перерахунок усієї таблиці.
/// </remarks>
public static class MethodologyFunctions
{
    /// <summary>Степінь; те саме, що оператор <c>^</c>.</summary>
    /// <param name="args">Основа і показник.</param>
    public static ExpressionValue Power(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 2, out var guard))
        {
            return guard;
        }

        var value = args[0].AsNumber()!.Value;
        var exponent = args[1].AsNumber()!.Value;

        try
        {
            // ⚠ Math.Pow повертає double і заборонений (D-30): 10^2 у ньому
            // дає 99.999999999999986, і звірка з еталоном розходиться на
            // числі, яке людина вважає точним.
            return ExpressionValue.Number(DecimalMath.Pow(value, exponent));
        }
        catch (Exception ex) when (ex is OverflowException or ArgumentException)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }
        catch (DivideByZeroException)
        {
            return ExpressionValue.Error(ExpressionErrors.DivideByZero);
        }
    }

    /// <summary>Квадратний корінь; від'ємний аргумент → <c>#VALUE</c>.</summary>
    /// <param name="args">Один аргумент.</param>
    public static ExpressionValue Sqrt(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 1, out var guard))
        {
            return guard;
        }

        var value = args[0].AsNumber()!.Value;

        // Від'ємний аргумент — це ЗНАЧЕННЯ #VALUE, а не виняток (02b §6.4).
        return value < 0m
            ? ExpressionValue.Error(ExpressionErrors.BadValue)
            : ExpressionValue.Number(DecimalMath.Sqrt(value));
    }

    /// <summary>Експонента.</summary>
    /// <param name="args">Один аргумент.</param>
    public static ExpressionValue Exp(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 1, out var guard))
        {
            return guard;
        }

        try
        {
            return ExpressionValue.Number(DecimalMath.Exp(args[0].AsNumber()!.Value));
        }
        catch (OverflowException)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }
    }

    /// <summary>Натуральний логарифм; аргумент ≤ 0 → <c>#VALUE</c>.</summary>
    /// <param name="args">Один аргумент.</param>
    public static ExpressionValue Ln(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 1, out var guard))
        {
            return guard;
        }

        var value = args[0].AsNumber()!.Value;

        return value <= 0m
            ? ExpressionValue.Error(ExpressionErrors.BadValue)
            : ExpressionValue.Number(DecimalMath.Ln(value));
    }

    /// <summary>Десятковий логарифм.</summary>
    /// <param name="args">Один аргумент.</param>
    public static ExpressionValue Log10(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 1, out var guard))
        {
            return guard;
        }

        var value = args[0].AsNumber()!.Value;

        return value <= 0m
            ? ExpressionValue.Error(ExpressionErrors.BadValue)
            : ExpressionValue.Number(DecimalMath.Log10(value));
    }

    /// <summary>Округлення вгору до кратного <c>significance</c>.</summary>
    /// <param name="args">Значення і, необов'язково, крок кратності.</param>
    /// <remarks>
    /// ⚠ Округлення до КРАТНОГО, а не до розряду: <c>CEILING(7, 5) = 10</c>,
    /// тоді як «до розряду» дало б 7. Це різні операції, і плутанина між ними
    /// дає число, що виглядає майже правильним.
    /// </remarks>
    public static ExpressionValue Ceiling(IReadOnlyList<ExpressionValue> args)
        => ToMultiple(args, up: true);

    /// <summary>Округлення вниз до кратного <c>significance</c>.</summary>
    /// <param name="args">Значення і, необов'язково, крок кратності.</param>
    public static ExpressionValue Floor(IReadOnlyList<ExpressionValue> args)
        => ToMultiple(args, up: false);

    /// <summary>Відкидання дробової частини — <b>не</b> округлення.</summary>
    /// <param name="args">Один аргумент.</param>
    /// <remarks>
    /// ⚠ <c>TRUNC(-1.7) = -1</c>, тоді як <c>ROUND(-1.7) = -2</c>. Плутанина
    /// тут дає розбіжність у знаку на від'ємних значеннях — і саме на них її
    /// найважче помітити.
    /// </remarks>
    public static ExpressionValue Trunc(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 1, out var guard))
        {
            return guard;
        }

        return ExpressionValue.Number(decimal.Truncate(args[0].AsNumber()!.Value));
    }

    /// <summary>Остача; знак результату — як у діленого.</summary>
    /// <param name="args">Ділене і дільник.</param>
    /// <remarks>
    /// ⚠ Знак — як у ДІЛЕНОГО (як у C# <c>%</c>), а не як у дільника (як в
    /// Excel): <c>MOD(-3, 5)</c> дає <c>-3</c>, а не <c>2</c>. Обидві
    /// конвенції поширені, і мовчазний вибір іншої змінив би знак у
    /// результатах, які вже подані.
    /// </remarks>
    public static ExpressionValue Mod(IReadOnlyList<ExpressionValue> args)
    {
        if (Guard(args, 2, out var guard))
        {
            return guard;
        }

        var divisor = args[1].AsNumber()!.Value;

        return divisor == 0m
            ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
            : ExpressionValue.Number(args[0].AsNumber()!.Value % divisor);
    }

    /// <summary>Перше не-<c>null</c> значення.</summary>
    /// <param name="args">Кандидати в порядку пріоритету.</param>
    public static ExpressionValue Coalesce(IReadOnlyList<ExpressionValue> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        foreach (var value in args)
        {
            // ⚠ Помилка (#DIV/0 тощо) НЕ є null і повертається як є. Пропустити
            // її означало б, що COALESCE тихо ховає зламане обчислення за
            // наступним значенням — перехоплює помилки лише IFERROR.
            if (value.IsError)
            {
                return value;
            }

            if (!value.IsNull)
            {
                return value;
            }
        }

        return ExpressionValue.Null;
    }

    /// <summary>Вибір за збігом; без збігу і без <c>default</c> → <c>null</c>.</summary>
    /// <param name="args"><c>SWITCH(значення, збіг₁, результат₁, …, типове?)</c>.</param>
    /// <remarks>
    /// Парність аргументів після першого визначає, чи є типове значення:
    /// парна кількість — пари без типового, непарна — останній аргумент
    /// типовий.
    /// </remarks>
    public static ExpressionValue Switch(IReadOnlyList<ExpressionValue> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count < 3)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var subject = args[0];
        if (subject.IsError)
        {
            return subject;
        }

        var pairs = args.Count - 1;
        var hasDefault = pairs % 2 == 1;
        var lastPair = hasDefault ? args.Count - 2 : args.Count - 1;

        // ⚠ Перший збіг, а не останній: перелік читається згори вниз, і
        // порядок у ньому — це пріоритет, який автор написав навмисно.
        for (var i = 1; i < lastPair; i += 2)
        {
            if (AreEqual(subject, args[i]))
            {
                return args[i + 1];
            }
        }

        // ⛔ Без збігу і без типового — null, а НЕ нуль. Нуль тут виглядав би
        // як виміряне значення і потрапив би в підсумок звіту як реальний.
        return hasDefault ? args[^1] : ExpressionValue.Null;
    }

    /// <summary>Значення для конкретної речовини в контексті методології.</summary>
    /// <param name="args">Код речовини.</param>
    /// <param name="context">Контекст обчислення.</param>
    /// <remarks>
    /// Речовина, якої немає серед <c>calc.MethodologySubstance</c> цієї
    /// версії, — помилка конфігурації, а не <c>null</c>: мовчазна порожнеча
    /// перетворила б забутий рядок довідника на нульовий викид.
    /// </remarks>
    public static ExpressionValue Substance(
        IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        if (args.Count != 1)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (args[0].IsError)
        {
            return args[0];
        }

        if (args[0].Value is not string code || string.IsNullOrWhiteSpace(code))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var value = context.GetFormulaResult(code);

        return value.IsNull ? ExpressionValue.Error(ExpressionErrors.BadReference) : value;
    }

    /// <summary>Округлення до кратного в заданий бік.</summary>
    private static ExpressionValue ToMultiple(IReadOnlyList<ExpressionValue> args, bool up)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count is < 1 or > 2)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (Propagate(args) is { } propagated)
        {
            return propagated;
        }

        if (args[0].AsNumber() is not { } value)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var significance = args.Count == 2 ? args[1].AsNumber() : 1m;
        if (significance is not { } step)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (step == 0m)
        {
            // Кратність нуля не визначена: будь-яке число кратне нулю лише
            // тоді, коли воно нуль.
            return value == 0m
                ? ExpressionValue.Number(0m)
                : ExpressionValue.Error(ExpressionErrors.DivideByZero);
        }

        var ratio = value / step;
        var rounded = up ? decimal.Ceiling(ratio) : decimal.Floor(ratio);

        return ExpressionValue.Number(rounded * step);
    }

    /// <summary>
    /// Перевіряє кількість аргументів і те, що всі вони числа.
    /// </summary>
    /// <returns><c>true</c>, якщо результат уже визначений і лежить у <paramref name="result"/>.</returns>
    private static bool Guard(IReadOnlyList<ExpressionValue> args, int arity, out ExpressionValue result)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count != arity)
        {
            result = ExpressionValue.Error(ExpressionErrors.BadValue);
            return true;
        }

        if (Propagate(args) is { } propagated)
        {
            result = propagated;
            return true;
        }

        if (args.Any(a => a.AsNumber() is null))
        {
            result = ExpressionValue.Error(ExpressionErrors.BadValue);
            return true;
        }

        result = ExpressionValue.Null;
        return false;
    }

    /// <summary>
    /// Помилка або <c>null</c> серед аргументів, які треба віддати як є.
    /// </summary>
    /// <remarks>
    /// Помилка поширюється (02b §6.4): <c>SQRT(1/0)</c> має дати <c>#DIV/0</c>,
    /// а не <c>#VALUE</c> — інакше причину видно не буде. <c>null</c> теж
    /// поширюється: корінь із «не заповнено» це «не заповнено», а не нуль.
    /// </remarks>
    private static ExpressionValue? Propagate(IReadOnlyList<ExpressionValue> args)
    {
        foreach (var value in args)
        {
            if (value.IsError)
            {
                return value;
            }
        }

        return args.Any(a => a.IsNull) ? ExpressionValue.Null : null;
    }

    /// <summary>Рівність значень для <c>SWITCH</c>.</summary>
    private static bool AreEqual(ExpressionValue left, ExpressionValue right)
    {
        if (left.IsNull || right.IsNull)
        {
            // Два null рівні між собою (E11), null і значення — ні.
            return left.IsNull && right.IsNull;
        }

        if (left.AsNumber() is { } leftNumber && right.AsNumber() is { } rightNumber)
        {
            return leftNumber == rightNumber;
        }

        // ⚠ Порівняння рядків ordinal, не за культурою: код речовини
        // `SUB-COD` не має залежати від того, у якій локалі запущено процес.
        return left.Value is string leftText && right.Value is string rightText
            ? string.Equals(leftText, rightText, StringComparison.Ordinal)
            : Equals(left.Value, right.Value);
    }
}
