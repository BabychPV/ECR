using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Обчислює AST. Уся арифметика — в <see cref="decimal"/>: порядок додавання
/// <c>float</c> змінює результат, і звірка з еталоном стає неможливою (D-30).
/// </summary>
/// <remarks>
/// ⛔ **Обчислювач знає ДІАЛЕКТ.** До кроку <c>I.14</c> він викликав функції
/// за одним каталогом на обидві мови, і <c>POWER(2;3)</c> у методології
/// рахувався нашим вигаданим набором, хоча чинний рушій такого імені не знає
/// (<c>Q-082</c>). Діалект береться з <c>ParsedExpression.Dialect</c> — тобто
/// з того самого розбору, який ім'я і прийняв: інакше приймати й рахувати
/// могли б різні мови.
/// </remarks>
/// <param name="functions">Каталог функцій діалекту шаблонів.</param>
/// <param name="arithmetic">
/// Арифметика, якою рахуються функції діалекту методологій.
/// </param>
public sealed class Evaluator(
    Functions.FunctionRegistry functions, IEvaluationArithmetic arithmetic)
{
    /// <summary>
    /// Обчислювач із арифметикою <see cref="StrictDecimalArithmetic"/>.
    /// </summary>
    /// <param name="functions">Каталог функцій діалекту шаблонів.</param>
    /// <remarks>
    /// ⚠ <c>Strict</c> тут — не рішення про режим версії, а те, що обчислювач
    /// робив і доти: наскрізний <c>decimal</c>. Підстановка
    /// <see cref="LegacyDoubleArithmetic"/> за <c>NumericMode</c> версії — крок
    /// <c>I.7</c> (<c>MaskedZero</c>); доти жоден шлях не просить іншої, і
    /// мовчазний вибір мусить збігатися з попередньою поведінкою, а не бути
    /// новим твердженням про числа.
    /// </remarks>
    public Evaluator(Functions.FunctionRegistry functions)
        : this(functions, new StrictDecimalArithmetic())
    {
    }

    /// <summary>Обчислює вираз у контексті.</summary>
    /// <param name="node">Корінь дерева.</param>
    /// <param name="context">Джерело даних.</param>
    /// <param name="dialect">
    /// Діалект — визначає, за яким каталогом викликаються функції.
    /// </param>
    public ExpressionValue Evaluate(
        AstNode node, IEvaluationContext context, ExpressionDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);

        var values = EvaluateGroup(node, context, dialect);

        // Скалярна позиція, а в неї потрапив діапазон: одного значення немає.
        // Порожній діапазон дає null, кілька значень — #VALUE, бо мовчки взяти
        // перший елемент означало б рахувати не те, що написано.
        return values.Count switch
        {
            1 => values[0],
            0 => ExpressionValue.Null,
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    /// <summary>
    /// Обчислює вузол як ГРУПУ значень: посилання-діапазон дає їх багато.
    /// </summary>
    private IReadOnlyList<ExpressionValue> EvaluateGroup(
        AstNode node, IEvaluationContext context, ExpressionDialect dialect)
        => node switch
        {
            CellReferenceNode reference => context.Read(reference),
            _ => [EvaluateScalar(node, context, dialect)],
        };

    private ExpressionValue EvaluateScalar(
        AstNode node, IEvaluationContext context, ExpressionDialect dialect)
        => node switch
        {
            LiteralNode literal => Literal(literal),
            UnaryNode unary => Unary(unary, context, dialect),
            BinaryNode binary => Binary(binary, context, dialect),
            ConditionalNode conditional => Conditional(conditional, context, dialect),
            FunctionNode function => Function(function, context, dialect),
            SymbolReferenceNode symbol => Symbol(symbol, context),
            PeriodPropertyNode period => Period(period, context),
            CellReferenceNode reference => Evaluate(reference, context, dialect),
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };

    private static ExpressionValue Literal(LiteralNode node)
        => node.Type switch
        {
            ExpressionValueType.Number => ExpressionValue.Number((decimal)node.Value!),
            ExpressionValueType.Text => ExpressionValue.Text((string)node.Value!),
            ExpressionValueType.Boolean => ExpressionValue.Boolean((bool)node.Value!),
            ExpressionValueType.Date => ExpressionValue.Date((DateTime)node.Value!),
            _ => ExpressionValue.Null,
        };

    private ExpressionValue Unary(
        UnaryNode node, IEvaluationContext context, ExpressionDialect dialect)
    {
        var operand = EvaluateScalar(node.Operand, context, dialect);
        if (operand.IsError)
        {
            return operand;
        }

        if (operand.IsNull)
        {
            return ExpressionValue.Null;
        }

        switch (node.Operator)
        {
            case UnaryOperator.Negate:
                return operand.AsNumber() is { } negate
                    ? ExpressionValue.Number(-negate)
                    : ExpressionValue.Error(ExpressionErrors.BadValue);

            case UnaryOperator.Plus:
                return operand.AsNumber() is not null
                    ? operand
                    : ExpressionValue.Error(ExpressionErrors.BadValue);

            case UnaryOperator.Not:
                return operand.Type == ExpressionValueType.Boolean
                    ? ExpressionValue.Boolean(!(bool)operand.Value!)
                    : ExpressionValue.Error(ExpressionErrors.BadValue);

            default:
                return ExpressionValue.Error(ExpressionErrors.BadValue);
        }
    }

    private ExpressionValue Binary(
        BinaryNode node, IEvaluationContext context, ExpressionDialect dialect)
    {
        var left = EvaluateScalar(node.Left, context, dialect);
        var right = EvaluateScalar(node.Right, context, dialect);

        // Помилка поширюється через операції: #DIV/0 + 1 = #DIV/0.
        // Перехопити її можна лише IFERROR (02b §6.4).
        if (left.IsError)
        {
            return left;
        }

        if (right.IsError)
        {
            return right;
        }

        switch (node.Operator)
        {
            // ⚠ ВИНЯТОК із правила поширення null: конкатенація трактує його
            // як порожній рядок. Інакше одна незаповнена комірка стирала б
            // увесь складений підпис.
            case BinaryOperator.Concat:
                return ExpressionValue.Text(AsText(left) + AsText(right));

            // ⚠ ДРУГИЙ виняток: рівність порівнює саму порожнечу.
            // null = null → TRUE, null = 1 → FALSE (02b §6.2).
            case BinaryOperator.Equal:
                return ExpressionValue.Boolean(AreEqual(left, right));

            case BinaryOperator.NotEqual:
                return ExpressionValue.Boolean(!AreEqual(left, right));
        }

        // ⚠ ТРЕТІЙ виняток, і найменш очевидний: ділення на null — це не
        // «невідомий результат», а неможлива операція, тому #DIV/0, а не null
        // (02b §6.4). Ділене при цьому null поширює як звичайно.
        if (node.Operator is BinaryOperator.Divide or BinaryOperator.Modulo && right.IsNull)
        {
            return ExpressionValue.Error(ExpressionErrors.DivideByZero);
        }

        // Скрізь далі null ПОШИРЮЄТЬСЯ: null * 0 = null, а НЕ 0. Нуль тут був
        // би стверджуванням «добуток точно нульовий», хоча множник невідомий.
        if (left.IsNull || right.IsNull)
        {
            return ExpressionValue.Null;
        }

        return node.Operator switch
        {
            BinaryOperator.Add => Arithmetic(left, right, node.Operator),
            BinaryOperator.Subtract => Arithmetic(left, right, node.Operator),
            BinaryOperator.Multiply => Arithmetic(left, right, node.Operator),
            BinaryOperator.Divide => Arithmetic(left, right, node.Operator),
            BinaryOperator.Modulo => Arithmetic(left, right, node.Operator),
            BinaryOperator.Power => Arithmetic(left, right, node.Operator),
            BinaryOperator.Less or BinaryOperator.LessOrEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterOrEqual => Compare(left, right, node.Operator),
            BinaryOperator.And or BinaryOperator.Or => Logic(left, right, node.Operator),
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    private static ExpressionValue Arithmetic(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        // Date − Date → Number (днів); Date + Number → Date (02b §5).
        if (left.Type == ExpressionValueType.Date || right.Type == ExpressionValueType.Date)
        {
            return DateArithmetic(left, right, op);
        }

        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        switch (op)
        {
            case BinaryOperator.Add:
                return ExpressionValue.Number(a + b);

            case BinaryOperator.Subtract:
                return ExpressionValue.Number(a - b);

            case BinaryOperator.Multiply:
                return ExpressionValue.Number(a * b);

            case BinaryOperator.Divide:
                // Ділення на нуль — ЗНАЧЕННЯ, а не виняток: одна зіпсована
                // комірка не має валити перерахунок усієї таблиці.
                return b == 0m
                    ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                    : ExpressionValue.Number(a / b);

            case BinaryOperator.Modulo:
                return b == 0m
                    ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                    : ExpressionValue.Number(a % b);

            case BinaryOperator.Power:
                return Power(a, b);

            default:
                return ExpressionValue.Error(ExpressionErrors.BadValue);
        }
    }

    /// <summary>
    /// Степінь у <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>Math.Pow</c> не використовується: він рахує в <c>double</c>, а
    /// <c>D-30</c> забороняє <c>float</c>/<c>double</c> в обчисленнях — звірка
    /// з еталоном після цього неможлива. Цілий показник підноситься множенням;
    /// дробовий у діалекті шаблонів не трапляється і дає <c>#VALUE</c>, а не
    /// приблизне число.
    /// </remarks>
    private static ExpressionValue Power(decimal value, decimal exponent)
    {
        if (exponent != decimal.Truncate(exponent))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var power = (int)exponent;
        var negative = power < 0;
        power = Math.Abs(power);

        var result = 1m;
        try
        {
            for (var i = 0; i < power; i++)
            {
                result *= value;
            }

            if (!negative)
            {
                return ExpressionValue.Number(result);
            }

            return result == 0m
                ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                : ExpressionValue.Number(1m / result);
        }
        catch (OverflowException)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }
    }

    private static ExpressionValue DateArithmetic(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        if (left.Type == ExpressionValueType.Date && right.Type == ExpressionValueType.Date)
        {
            return op == BinaryOperator.Subtract
                ? ExpressionValue.Number((decimal)((DateTime)left.Value! - (DateTime)right.Value!).TotalDays)
                : ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (left.Type == ExpressionValueType.Date && right.AsNumber() is { } days)
        {
            return op switch
            {
                BinaryOperator.Add => ExpressionValue.Date(((DateTime)left.Value!).AddDays((double)days)),
                BinaryOperator.Subtract => ExpressionValue.Date(((DateTime)left.Value!).AddDays(-(double)days)),
                _ => ExpressionValue.Error(ExpressionErrors.BadValue),
            };
        }

        return ExpressionValue.Error(ExpressionErrors.BadValue);
    }

    private static ExpressionValue Compare(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        int order;
        if (left.AsNumber() is { } a && right.AsNumber() is { } b)
        {
            order = a.CompareTo(b);
        }
        else if (left.Type == ExpressionValueType.Text && right.Type == ExpressionValueType.Text)
        {
            order = string.CompareOrdinal((string)left.Value!, (string)right.Value!);
        }
        else if (left.Type == ExpressionValueType.Date && right.Type == ExpressionValueType.Date)
        {
            order = ((DateTime)left.Value!).CompareTo((DateTime)right.Value!);
        }
        else
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return ExpressionValue.Boolean(op switch
        {
            BinaryOperator.Less => order < 0,
            BinaryOperator.LessOrEqual => order <= 0,
            BinaryOperator.Greater => order > 0,
            _ => order >= 0,
        });
    }

    private static ExpressionValue Logic(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        if (left.Type != ExpressionValueType.Boolean || right.Type != ExpressionValueType.Boolean)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var a = (bool)left.Value!;
        var b = (bool)right.Value!;
        return ExpressionValue.Boolean(op == BinaryOperator.And ? a && b : a || b);
    }

    private static bool AreEqual(ExpressionValue left, ExpressionValue right)
    {
        if (left.IsNull || right.IsNull)
        {
            return left.IsNull && right.IsNull;
        }

        if (left.AsNumber() is { } a && right.AsNumber() is { } b)
        {
            return a == b;
        }

        if (left.Type != right.Type)
        {
            return false;
        }

        return Equals(left.Value, right.Value);
    }

    private ExpressionValue Conditional(
        ConditionalNode node, IEvaluationContext context, ExpressionDialect dialect)
    {
        var condition = EvaluateScalar(node.Condition, context, dialect);
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

        // Обчислюється ЛИШЕ обрана гілка: інакше `x = 0 ? 0 : 1/x` давав би
        // #DIV/0 саме тоді, коли автор виразу від нього захищався.
        return (bool)condition.Value!
            ? EvaluateScalar(node.WhenTrue, context, dialect)
            : EvaluateScalar(node.WhenFalse, context, dialect);
    }

    private ExpressionValue Function(
        FunctionNode node, IEvaluationContext context, ExpressionDialect dialect)
        => dialect == ExpressionDialect.Methodology
            ? MethodologyCall(node, context)
            : TemplateCall(node, context);

    private ExpressionValue TemplateCall(FunctionNode node, IEvaluationContext context)
    {
        // IFERROR обчислює запасну гілку тільки за потреби — інакше вона могла б
        // сама впасти й перетворити перехоплення на нову помилку.
        if (node.Name.Equals("IFERROR", StringComparison.OrdinalIgnoreCase) && node.Arguments.Count == 2)
        {
            var value = Evaluate(node.Arguments[0], context, ExpressionDialect.Template);
            return value.IsError
                ? Evaluate(node.Arguments[1], context, ExpressionDialect.Template)
                : value;
        }

        var groups = new List<IReadOnlyList<ExpressionValue>>(node.Arguments.Count);
        foreach (var argument in node.Arguments)
        {
            groups.Add(EvaluateGroup(argument, context, ExpressionDialect.Template));
        }

        return functions.Invoke(node.Name, groups, context);
    }

    /// <summary>
    /// Виклик функції діалекту методологій — за <c>DialectCatalog</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Групування аргументів тут не потрібне: у діалекті методологій немає
    /// діапазонів за побудовою мови (<c>02b</c> §8), операнди — скаляри
    /// <c>@Arg</c>, <c>CST.X</c>, <c>!Formula</c>.
    ///
    /// ⛔ <c>if</c> і <c>ifs</c> обробляються ДО обчислення аргументів, і це не
    /// оптимізація. У NCalc гілки обчислюються ліниво, тому
    /// <c>if(@Volume = 0, 0, @Mass / @Volume)</c> у чинній системі не давав
    /// ділення на нуль. Порахувати обидві гілки означало б отримати
    /// нескінченність саме там, де автор від неї захищався.
    /// </remarks>
    private ExpressionValue MethodologyCall(FunctionNode node, IEvaluationContext context)
    {
        if (string.Equals(node.Name, "if", StringComparison.Ordinal))
        {
            return If(node.Arguments, context);
        }

        if (string.Equals(node.Name, "ifs", StringComparison.Ordinal))
        {
            return Ifs(node.Arguments, context);
        }

        var args = new List<ExpressionValue>(node.Arguments.Count);
        foreach (var argument in node.Arguments)
        {
            args.Add(EvaluateScalar(argument, context, ExpressionDialect.Methodology));
        }

        return Functions.MethodologyFunctions.Invoke(node.Name, args, arithmetic, context);
    }

    /// <summary>
    /// <c>if(cond, then, else)</c> — строго тримісний, гілки ліниві.
    /// </summary>
    /// <remarks>
    /// ⚠ Повертає і ТЕКСТ: у корпусі це <c>'В пределе норматива'</c>,
    /// <c>'Сверхнорматив'</c>. Тому тип результату в каталозі — <c>Null</c>
    /// («тип обраної гілки»), а не <c>Number</c>.
    /// </remarks>
    private ExpressionValue If(IReadOnlyList<AstNode> args, IEvaluationContext context)
    {
        if (args.Count != 3)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var condition = EvaluateScalar(args[0], context, ExpressionDialect.Methodology);
        if (Branch(condition) is not { } taken)
        {
            return condition.IsError || condition.IsNull
                ? condition
                : ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return EvaluateScalar(args[taken ? 1 : 2], context, ExpressionDialect.Methodology);
    }

    /// <summary>
    /// <c>ifs(c₁, v₁, …, типове?)</c> — перша істинна умова виграє.
    /// </summary>
    /// <remarks>
    /// ⛔ Ярус <c>Extension</c>: у NCalc 1.3.8 такої функції НЕМАЄ — виміряно
    /// (<c>tests/Ecr.Legacy.Probe</c>), хоча директива №05 §3 називає її серед
    /// 24. Тому у версії з <c>NumericMode = Legacy</c> її відхиляє публікація
    /// (<c>ECR-CALC-0433</c>): відтворювати їй нічого.
    ///
    /// ⚠ Без жодної істинної умови і без типового — <c>null</c>, а НЕ нуль.
    /// Нуль тут виглядав би як виміряне значення і потрапив би в підсумок
    /// звіту як реальний.
    /// </remarks>
    private ExpressionValue Ifs(IReadOnlyList<AstNode> args, IEvaluationContext context)
    {
        if (args.Count < 2)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var pairs = args.Count / 2;
        for (var i = 0; i < pairs; i++)
        {
            var condition = EvaluateScalar(args[i * 2], context, ExpressionDialect.Methodology);
            if (Branch(condition) is not { } taken)
            {
                return condition.IsError || condition.IsNull
                    ? condition
                    : ExpressionValue.Error(ExpressionErrors.BadValue);
            }

            if (taken)
            {
                return EvaluateScalar(args[(i * 2) + 1], context, ExpressionDialect.Methodology);
            }
        }

        // Непарна кількість аргументів — останній типовий.
        return args.Count % 2 == 1
            ? EvaluateScalar(args[^1], context, ExpressionDialect.Methodology)
            : ExpressionValue.Null;
    }

    /// <summary>
    /// Яку гілку обрати; <c>null</c> — умова не булева або зламана.
    /// </summary>
    private static bool? Branch(ExpressionValue condition)
        => condition.Type == ExpressionValueType.Boolean ? (bool)condition.Value! : null;

    private static ExpressionValue Symbol(SymbolReferenceNode node, IEvaluationContext context)
        => node.Kind switch
        {
            SymbolKind.Argument => context.GetArgument(node.Name),
            SymbolKind.Constant => context.GetConstant(node.Name),
            SymbolKind.Formula => context.GetFormulaResult(node.Name),
            SymbolKind.Header => context.GetHeader(node.Name),
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };

    private static ExpressionValue Period(PeriodPropertyNode node, IEvaluationContext context)
    {
        var period = context.Period;
        return node.Property.ToUpperInvariant() switch
        {
            "DAYS" => ExpressionValue.Number(period.Days),
            "HOURS" => ExpressionValue.Number(period.Hours),
            "SECONDS" => ExpressionValue.Number(period.Seconds),
            "START" => ExpressionValue.Date(period.Start.ToDateTime(TimeOnly.MinValue)),
            "END" => ExpressionValue.Date(period.End.ToDateTime(TimeOnly.MinValue)),
            "YEAR" => ExpressionValue.Number(period.Year),
            "SEQUENCE" => ExpressionValue.Number(period.Sequence),
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    private static string AsText(ExpressionValue value)
        => value.Type switch
        {
            ExpressionValueType.Null => string.Empty,
            ExpressionValueType.Text => (string)value.Value!,
            ExpressionValueType.Number => ((decimal)value.Value!).ToString(CultureInfo.InvariantCulture),
            ExpressionValueType.Boolean => (bool)value.Value! ? "TRUE" : "FALSE",
            ExpressionValueType.Date => ((DateTime)value.Value!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value.ErrorCode ?? string.Empty,
        };
}
