using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// Виклик функції діалекту <c>Methodology</c> — за <see cref="DialectCatalog"/>,
/// тобто за ВИМІРЯНИМ набором NCalc 1.3.8.
/// </summary>
/// <remarks>
/// ⛔ **Тут більше немає ані <c>POWER</c>, ані <c>SWITCH</c>, ані
/// <c>COALESCE</c>, ані <c>MOD</c>, ані <c>TRUNC</c>.** Той набір (<c>02b</c>
/// §8 у старій редакції) був вигаданий цілком, а формули з ним наш рушій
/// приймав і рахував — при тому, що чинна система не рахувала їх ніколи
/// (<c>Q-082</c>). Заміни названі в <c>DialectCatalog.Advice</c> і в
/// <c>02b</c> §8: степінь — <c>Pow</c>, остача — оператор <c>%</c>,
/// вибір — вкладені <c>if</c>.
///
/// ⛔ Числа рахує НЕ цей файл, а <see cref="IEvaluationArithmetic"/>: там
/// живуть обидві семантики (<c>Legacy</c> у <c>double</c> за NCalc 1.3.8,
/// <c>Strict</c> у <c>decimal</c>), і друга реалізація тих самих двадцяти
/// функцій розійшлася б із першою — видимо лише як інше число в шостому знаку
/// звірки. Тут лишається те, чого арифметика не знає: поширення помилок,
/// множинна належність <c>in</c>, одиниці й речовини.
///
/// ⚠ Помилка тут завжди **значення**, ніколи виняток (<c>02b</c> §6.4): одна
/// зіпсована комірка не має валити перерахунок усієї таблиці.
///
/// ⛔ Функцій поточного часу (<c>NOW</c>, <c>TODAY</c>), випадковості,
/// введення-виведення і звернень до БД у діалекті немає. Час береться лише з
/// календарного контексту (<c>02b</c> §10) — інакше результат перерахунку
/// залежав би від дня, коли його запустили, і звірка з еталоном стала б
/// неможливою.
/// </remarks>
public static class MethodologyFunctions
{
    /// <summary>Одномісні числові функції каталогу — рахує арифметика режиму.</summary>
    /// <remarks>
    /// ⚠ <c>Ceiling</c>, <c>Floor</c> і <c>Truncate</c> строго одномісні:
    /// другого аргументу <c>significance</c> в NCalc 1.3.8 немає (виміряно).
    /// Наш попередній каталог дозволяв <c>CEILING(a, significance)</c> — і це
    /// давало інше число там, де формула виглядала знайомою.
    /// </remarks>
    private static readonly HashSet<string> UnaryNumeric = new(StringComparer.Ordinal)
    {
        "Abs", "Acos", "Asin", "Atan", "Ceiling", "Cos", "Exp", "Floor",
        "Ln", "Log10", "Sign", "Sin", "Sqrt", "Tan", "Truncate",
    };

    /// <summary>Двомісні числові функції каталогу.</summary>
    /// <remarks>
    /// ⚠ <c>Max</c> і <c>Min</c> саме БІНАРНІ, а не варіативні: у корпусі
    /// чинної системи вони вкладені до дванадцяти рівнів саме тому.
    /// </remarks>
    private static readonly HashSet<string> BinaryNumeric = new(StringComparer.Ordinal)
    {
        "IEEERemainder", "Log", "Max", "Min", "Pow",
    };

    /// <summary>Обчислює виклик функції діалекту методологій.</summary>
    /// <param name="name">Ім'я функції — <b>з урахуванням регістру</b>.</param>
    /// <param name="args">Обчислені аргументи.</param>
    /// <param name="arithmetic">Арифметика режиму версії методології.</param>
    /// <param name="context">Контекст — потрібен <c>CONVERT</c> і <c>SUBSTANCE</c>.</param>
    /// <returns>Значення або значення-помилка.</returns>
    /// <exception cref="InvalidOperationException">
    /// Імені немає в каталозі. Сюди не потрапити з розібраного виразу: розбір
    /// відхиляє невідоме ім'я ще при публікації.
    /// </exception>
    public static ExpressionValue Invoke(
        string name,
        IReadOnlyList<ExpressionValue> args,
        IEvaluationArithmetic arithmetic,
        IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(arithmetic);
        ArgumentNullException.ThrowIfNull(context);

        // ⛔ `in` перевіряється ДО поширення null: належність порожньому
        // значенню — це FALSE, а не «невідомо». Поширити null тут означало б,
        // що незаповнений аргумент вимикає цілу гілку `if`, замість піти в
        // «інакше».
        if (string.Equals(name, "in", StringComparison.Ordinal))
        {
            return In(args);
        }

        // CONVERT сам вирішує, що робити з null і помилкою: для нього «не
        // заповнено в тоннах» — це «не заповнено в кілограмах», а не помилка.
        if (string.Equals(name, "CONVERT", StringComparison.Ordinal))
        {
            return ConvertFunction.Invoke(args, context);
        }

        if (string.Equals(name, "SUBSTANCE", StringComparison.Ordinal))
        {
            return Substance(args, context);
        }

        if (Propagate(args) is { } propagated)
        {
            return propagated;
        }

        if (string.Equals(name, "Round", StringComparison.Ordinal))
        {
            return Round(args, arithmetic);
        }

        if (UnaryNumeric.Contains(name))
        {
            return arithmetic.Unary(args[0], name);
        }

        if (BinaryNumeric.Contains(name))
        {
            return arithmetic.Binary(args[0], args[1], name);
        }

        throw new InvalidOperationException(
            $"Функція '{name}' не входить у набір діалекту методологій.");
    }

    /// <summary>
    /// <c>Round(a, digits)</c> — округлення до заданої кількості знаків.
    /// </summary>
    /// <remarks>
    /// ⛔ Правило середини бере АРИФМЕТИКА, а не цей метод: у <c>Legacy</c>
    /// воно банківське (чинна збірка створює <c>NCalc.Expression</c> без
    /// <c>EvaluateOptions</c>, тож <c>RoundAwayFromZero</c> не виставлений —
    /// виміряно: <c>Round(2.5,0) = 2</c>), у <c>Strict</c> — від нуля. Друга
    /// точка задання дала б інше число на кожному «.5».
    ///
    /// ⚠ Дробова кількість знаків — <c>#VALUE</c>, а не мовчазне відкидання:
    /// <c>Round(x, 2.7)</c> написали не для того, щоб отримати два знаки.
    /// </remarks>
    private static ExpressionValue Round(
        IReadOnlyList<ExpressionValue> args, IEvaluationArithmetic arithmetic)
    {
        if (args[1].AsNumber() is not { } digits || digits != decimal.Truncate(digits))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // ⚠ Межа 0…28 — не захист, а межа `decimal.Round`: поза нею BCL кидає,
        // а виняток тут перетворив би зіпсовану комірку на впалий перерахунок.
        return digits is < 0m or > 28m
            ? ExpressionValue.Error(ExpressionErrors.BadValue)
            : arithmetic.Round(args[0], (int)digits);
    }

    /// <summary>
    /// <c>in(x, a, b, …)</c> — належність множині; працює і з рядками.
    /// </summary>
    /// <remarks>
    /// ⚠ Порівняння рядків ПОРЯДКОВЕ і з урахуванням регістру: у корпусі це
    /// значення довідника (<c>'No - Нет'</c>), а не текст користувача, і
    /// зведення регістру перевело б рядки з однієї гілки <c>if</c> в іншу.
    /// </remarks>
    private static ExpressionValue In(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count < 2)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // Помилка поширюється: `in(1/0, 1, 2)` має лишитися #DIV/0, інакше
        // зламане обчислення тихо стане відповіддю «не належить».
        foreach (var value in args)
        {
            if (value.IsError)
            {
                return value;
            }
        }

        for (var i = 1; i < args.Count; i++)
        {
            if (AreEqual(args[0], args[i]))
            {
                return ExpressionValue.Boolean(true);
            }
        }

        return ExpressionValue.Boolean(false);
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

    /// <summary>
    /// Помилка або <c>null</c> серед аргументів, які треба віддати як є.
    /// </summary>
    /// <remarks>
    /// Помилка поширюється (02b §6.4): <c>Sqrt(1/0)</c> має дати <c>#DIV/0</c>,
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

    /// <summary>Рівність значень для <c>in</c>.</summary>
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
