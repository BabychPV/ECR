using System.Collections.Frozen;
using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Перевіряє типи **при публікації**. Приведення не відбувається мовчки:
/// Excel вгадує тип і саме тому дає «майже правильні» числа; тут
/// неоднозначність — помилка, поки її ще дешево виправити (02b §5).
/// </summary>
/// <param name="signatures">
/// Каталог функцій діалекту; <c>null</c> — діалект шаблонів. Діалект звітів
/// передає <see cref="ReportFunctions.Find"/>: без нього його <c>IN</c> мав би тип
/// <c>Null</c>, і умова <c>when</c> із ним проходила б за будь-який очікуваний тип.
/// </param>
/// <remarks>
/// ✎ RT-21: функції довідників (перевірки 15–19 і попередження 21а, `02b` §12,
/// FEATURE-REGISTRY-TABLES §5.5). Форми довідників приходять із
/// <see cref="IRegistryBindingContext.Registries"/>; без них перевіряються лише
/// ті правила, яким форма не потрібна: код довідника — літерал (15) і
/// <c>EntryRef</c> поза арифметикою (19). Перевірку 18 (<c>ROW.</c>/<c>THIS</c>
/// поза областю) робить парсер (RT-07) — тут вона не дублюється.
/// </remarks>
public sealed class TypeChecker(Func<string, FunctionSignature?>? signatures = null)
{
    private static readonly FunctionRegistry Functions = new();

    /// <summary>
    /// Функції, яким <c>EntryRef</c> аргументом законний: гілки й перелік
    /// (<c>in</c>, §5.3) передають його далі, функції довідників його і чекають.
    /// Будь-яка інша функція (<c>Abs</c>, <c>CONVERT</c>, <c>SUM</c>…) — це
    /// арифметика над id запису, тобто <c>expr.entryRefMisuse</c>.
    /// </summary>
    private static readonly FrozenSet<string> EntryRefPassThrough = new[]
    {
        "IF", "IFERROR", "IFS", "IN",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Агрегати, фільтр яких отримує попередження 21а.</summary>
    private static readonly FrozenSet<string> Aggregates = new[]
    {
        RegistryForms.Sum, RegistryForms.Average, RegistryForms.Minimum, RegistryForms.Maximum, RegistryForms.Count,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Виводить тип виразу і збирає діагностики.</summary>
    /// <param name="node">Вузол виразу.</param>
    /// <param name="context">Джерело типів для посилань.</param>
    /// <param name="diagnostics">Куди складати зауваження публікації.</param>
    /// <returns>
    /// Тип результату. <c>EntryRef</c> повертається як <c>Number</c> — у рантаймі
    /// це id запису (§5.3), і саме так його зберігає формула.
    /// </returns>
    public ExpressionValueType Check(AstNode node, ITypeContext context, List<ExpressionDiagnostic> diagnostics)
        => Check(node, context, diagnostics, warnings: null);

    /// <summary>Виводить тип виразу, збирає діагностики й попередження.</summary>
    /// <param name="node">Вузол виразу.</param>
    /// <param name="context">Джерело типів для посилань.</param>
    /// <param name="diagnostics">Помилки публікації.</param>
    /// <param name="warnings">
    /// Попередження, що НЕ зупиняють публікацію (21а,
    /// <c>expr.registryScanUnindexed</c>); <c>null</c> — не збирати.
    /// </param>
    /// <remarks>
    /// ⚠ Попередження — окремий список, а не <paramref name="diagnostics"/>:
    /// усе, що лежить там, викликач тлумачить як відмову публікації
    /// (<c>PublishChecks</c>). Код попередження порожній — у каталозі помилок
    /// його немає (`02b` §12, рядок 21а: «—»).
    /// </remarks>
    public ExpressionValueType Check(
        AstNode node,
        ITypeContext context,
        List<ExpressionDiagnostic> diagnostics,
        List<ExpressionDiagnostic>? warnings)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var walk = new Walk(context, diagnostics, warnings);

        // Правило довідника: `ROW.` на верхньому рівні — запис, що
        // перевіряється (§6). Поза правилом області немає, і `ROW.` там уже
        // відхилив парсер (перевірка 18).
        var ruleScope = context.RuleRegistryCode is { } rule && context.Registries is { } registries
            ? registries.FindRegistry(rule)
            : null;

        return Infer(node, walk, ruleScope).Type;
    }

    private Typed Infer(AstNode node, Walk walk, RegistryShape? row)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        if (!TraversalStackGuard.TryEnter(node, walk.Diagnostics))
        {
            return Typed.Of(ExpressionValueType.Null);
        }

        var context = walk.Context;

        switch (node)
        {
            case LiteralNode literal:
                return Typed.Of(literal.Type);

            case CellReferenceNode reference:
                return context.GetReferenceRegistry(reference) is { } lookupTarget
                    ? Typed.Entry(lookupTarget)
                    : Typed.Of(context.GetReferenceType(reference));

            case SymbolReferenceNode symbol:
                return symbol.Kind switch
                {
                    SymbolKind.Argument => context.GetArgumentRegistry(symbol.Name) is { } argumentTarget
                        ? Typed.Entry(argumentTarget)
                        : Typed.Of(context.GetArgumentType(symbol.Name)),

                    // ⛔ Тут стояло `ExpressionValueType.Number` — «константа
                    // завжди число». Після появи `ConstantKind` це неправда:
                    // з 6507 констант корпусу 108 нечислові, і ~90 із них
                    // ужиті операндом порівняння. Жорстке `Number` робило б
                    // `CST.k1_CategorySelection_ = 'Summer'` «порівнянням
                    // різних типів», тобто ~90 хибних помилок публікації —
                    // щойно перевірку типів увімкнуть для методологій.
                    SymbolKind.Constant => Typed.Of(context.GetConstantType(symbol.Name)),
                    SymbolKind.Formula => context.GetFormulaRegistry(symbol.Name) is { } formulaTarget
                        ? Typed.Entry(formulaTarget)
                        : Typed.Of(ExpressionValueType.Null),
                    _ => Typed.Of(ExpressionValueType.Null),
                };

            case PeriodPropertyNode period:
                return Typed.Of(period.Property.ToUpperInvariant() is "START" or "END"
                    ? ExpressionValueType.Date
                    : ExpressionValueType.Number);

            case RowFieldNode rowField:
                return InferRowField(rowField, walk, row);

            case ThisNode:
                return context.RuleRegistryCode is { } rule ? Typed.Entry(rule) : Typed.Of(ExpressionValueType.Null);

            case UnaryNode unary:
                return CheckUnary(unary, walk, row);

            case BinaryNode binary:
                return CheckBinary(binary, walk, row);

            case ConditionalNode conditional:
                return CheckConditional(conditional, walk, row);

            case FunctionNode function:
                return CheckFunction(function, walk, row);

            default:
                return Typed.Of(ExpressionValueType.Null);
        }
    }

    private Typed CheckUnary(UnaryNode node, Walk walk, RegistryShape? row)
    {
        var operand = Infer(node.Operand, walk, row);

        if (node.Operator == UnaryOperator.Not)
        {
            Require(operand.Type, ExpressionValueType.Boolean, node, walk.Diagnostics,
                "expr.type.notNeedsBoolean", null, "Negation applies only to a Boolean value.");
            return Typed.Of(ExpressionValueType.Boolean);
        }

        if (operand.IsEntry)
        {
            EntryRefMisuse(node, operand, walk.Diagnostics);
            return Typed.Of(ExpressionValueType.Number);
        }

        Require(operand.Type, ExpressionValueType.Number, node, walk.Diagnostics,
            "expr.type.signNeedsNumber", null, "A unary sign applies only to a number.");
        return Typed.Of(ExpressionValueType.Number);
    }

    private Typed CheckBinary(BinaryNode node, Walk walk, RegistryShape? row)
    {
        var left = Infer(node.Left, walk, row);
        var right = Infer(node.Right, walk, row);
        var diagnostics = walk.Diagnostics;

        switch (node.Operator)
        {
            // Text & будь-що: друге приводиться до тексту інваріантно. Це
            // єдине дозволене неявне приведення в мові.
            case BinaryOperator.Concat:
                return Typed.Of(ExpressionValueType.Text);

            case BinaryOperator.Add:
            case BinaryOperator.Subtract:
            {
                if (left.IsEntry || right.IsEntry)
                {
                    EntryRefMisuse(node, left.IsEntry ? left : right, diagnostics);
                    return Typed.Of(ExpressionValueType.Number);
                }

                if (Is(left.Type, ExpressionValueType.Date) && Is(right.Type, ExpressionValueType.Date))
                {
                    // Date − Date → Number (днів); Date + Date не має сенсу.
                    if (node.Operator == BinaryOperator.Add)
                    {
                        Report(diagnostics, node,
                            "expr.type.datesNotAdded", null,
                            "Dates cannot be added; the difference of two dates is a number of days.");
                    }

                    return Typed.Of(ExpressionValueType.Number);
                }

                if (Is(left.Type, ExpressionValueType.Date) && Is(right.Type, ExpressionValueType.Number))
                {
                    return Typed.Of(ExpressionValueType.Date);
                }

                RequireNumeric(left, node, diagnostics);
                RequireNumeric(right, node, diagnostics);
                return Typed.Of(ExpressionValueType.Number);
            }

            case BinaryOperator.Multiply:
            case BinaryOperator.Divide:
            case BinaryOperator.Modulo:
            case BinaryOperator.Power:
                // ⚠ Одне зауваження на вузол, навіть коли EntryRef з обох боків.
                if (left.IsEntry || right.IsEntry)
                {
                    EntryRefMisuse(node, left.IsEntry ? left : right, diagnostics);
                    return Typed.Of(ExpressionValueType.Number);
                }

                RequireNumeric(left, node, diagnostics);
                RequireNumeric(right, node, diagnostics);
                return Typed.Of(ExpressionValueType.Number);

            case BinaryOperator.And:
            case BinaryOperator.Or:
                Require(left.Type, ExpressionValueType.Boolean, node, diagnostics,
                    "expr.type.logicalNeedsBoolean", null, "A logical operation needs Boolean operands.");
                Require(right.Type, ExpressionValueType.Boolean, node, diagnostics,
                    "expr.type.logicalNeedsBoolean", null, "A logical operation needs Boolean operands.");
                return Typed.Of(ExpressionValueType.Boolean);

            default:
            {
                if (left.IsEntry || right.IsEntry)
                {
                    CompareEntries(node, left, right, diagnostics);
                    return Typed.Of(ExpressionValueType.Boolean);
                }

                // Порівняння різних типів — помилка ПУБЛІКАЦІЇ. Excel тут
                // вгадав би, порівнявши число з текстом за кодами символів, і
                // дав би відповідь, яка виглядає осмисленою.
                if (!Comparable(left.Type, right.Type))
                {
                    Report(diagnostics, node,
                        "expr.type.incomparable",
                        DiagnosticParams.Of(("left", left.Type.ToString()), ("right", right.Type.ToString())),
                        $"Comparison of incompatible types: {left.Type} and {right.Type}.");
                }

                return Typed.Of(ExpressionValueType.Boolean);
            }
        }
    }

    /// <summary>
    /// Порівняння, в якому бере участь <c>EntryRef</c> (перевірка 19, §5.3).
    /// </summary>
    /// <remarks>
    /// ⛔ Дозволено рівно <c>=</c>/<c>&lt;&gt;</c> з <c>EntryRef</c> ТОГО САМОГО
    /// довідника. У рантаймі обидва боки — числа (id записів), тож
    /// <c>ROW.COMPONENT = 5</c> чи <c>ROW.CASE = REGFIND('COMPONENT', …)</c>
    /// рахувалися б мовчки й давали б «нічого не знайдено» — порожню суму, яка
    /// виглядає як законний нуль.
    ///
    /// ⚠ Порівняння з ТЕКСТОМ перевірка 19 не забороняє (§5.3 називає лише
    /// арифметику й число): так пишуться наявні предикати на <c>Lookup</c>-колонці
    /// за кодом, <c>[WHERE [WasteType] = 'W-01']</c> (`Q-072`).
    /// </remarks>
    private static void CompareEntries(BinaryNode node, Typed left, Typed right, List<ExpressionDiagnostic> diagnostics)
    {
        var entry = left.IsEntry ? left : right;
        var other = left.IsEntry ? right : left;
        var equality = node.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual;

        if (!equality)
        {
            EntryRefMisuse(node, entry, diagnostics);
            return;
        }

        if (other.IsEntry)
        {
            if (!string.Equals(entry.Registry, other.Registry, StringComparison.OrdinalIgnoreCase))
            {
                EntryRefMisuse(node, entry, diagnostics);
            }

            return;
        }

        switch (other.Type)
        {
            case ExpressionValueType.Null:
            case ExpressionValueType.Text:
                return;

            case ExpressionValueType.Number:
                EntryRefMisuse(node, entry, diagnostics);
                return;

            default:
                Report(diagnostics, node,
                    "expr.type.incomparable",
                    DiagnosticParams.Of(("left", Describe(left)), ("right", Describe(right))),
                    $"Comparison of incompatible types: {Describe(left)} and {Describe(right)}.");
                return;
        }
    }

    private Typed CheckConditional(ConditionalNode node, Walk walk, RegistryShape? row)
    {
        var condition = Infer(node.Condition, walk, row);
        Require(condition.Type, ExpressionValueType.Boolean, node, walk.Diagnostics,
            "expr.type.conditionNeedsBoolean", null, "The condition must be Boolean.");

        var whenTrue = Infer(node.WhenTrue, walk, row);
        var whenFalse = Infer(node.WhenFalse, walk, row);

        return IsNull(whenTrue) ? whenFalse : whenTrue;
    }

    private Typed CheckFunction(FunctionNode node, Walk walk, RegistryShape? row)
    {
        var upper = node.Name.ToUpperInvariant();

        switch (upper)
        {
            case RegistryForms.Field:
                return CheckRegistryField(node, walk, row);

            case RegistryForms.Find:
                return CheckRegistryFind(node, walk, row);

            case RegistryForms.One:
            case RegistryForms.Sum:
            case RegistryForms.Average:
            case RegistryForms.Minimum:
            case RegistryForms.Maximum:
            case RegistryForms.Count:
                return CheckRegistryScan(node, upper, walk, row);
        }

        var diagnostics = walk.Diagnostics;
        var arguments = node.Arguments.Select(a => Infer(a, walk, row)).ToList();

        // ⛔ Аудит L7-05: `AcceptsRange: false` досі ніде не перевірявся —
        // `ROUND([T].[r1:r3].[X], 2)` публікувався і рахувався як ROUND(v1, v2).
        if ((signatures ?? Functions.GetSignature)(node.Name) is { AcceptsRange: false })
        {
            foreach (var argument in node.Arguments)
            {
                if (argument is CellReferenceNode { Row: RowSelector.Range or RowSelector.Predicate })
                {
                    Report(diagnostics, argument, "expr.rangeNotAccepted",
                        DiagnosticParams.Of(("function", node.Name)),
                        $"{node.Name} takes a single value, not a range of rows. Wrap the range in SUM, AVERAGE, MIN or MAX.");
                }
            }
        }

        switch (upper)
        {
            case "IF" when arguments.Count == 3:
                // Умова IF має бути Boolean — це помилка ПУБЛІКАЦІЇ, а не
                // рантайму: інакше «IF(значення; …)» тихо йшов би однією гілкою.
                Require(arguments[0].Type, ExpressionValueType.Boolean, node, diagnostics,
                    "expr.type.ifNeedsCondition", null, "The first argument of IF must be a condition.");
                return IsNull(arguments[1]) ? arguments[2] : arguments[1];

            case "IFERROR" when arguments.Count == 2:
                return IsNull(arguments[0]) ? arguments[1] : arguments[0];

            case "SUM" or "AVERAGE" or "MIN" or "MAX" or "PRODUCT" or "ROUND" or "ABS" or "SUMIF":
                foreach (var type in arguments)
                {
                    RequireNumeric(type, node, diagnostics);
                }

                return Typed.Of(ExpressionValueType.Number);

            default:
                if (!EntryRefPassThrough.Contains(upper) && arguments.FirstOrDefault(a => a.IsEntry) is { IsEntry: true } entry)
                {
                    EntryRefMisuse(node, entry, diagnostics);
                }

                return Typed.Of((signatures ?? Functions.GetSignature)(node.Name)?.ResultType ?? ExpressionValueType.Null);
        }
    }

    /// <summary>
    /// <c>REGFIELD(entry, 'p')</c>: шлях поля від довідника запису (перевірка 16,
    /// закриття <c>Д-5</c>) і тип значення за полем (§5.3).
    /// </summary>
    /// <remarks>
    /// ⚠ Довідник запису відомий статично не завжди: <c>Lookup</c>-колонка без
    /// відомої цілі чи обчислений шлях — тоді тип <c>Null</c> (сумісний з усім),
    /// як і було до RT-21. Невідомий довідник тут НЕ звітується: про нього вже
    /// сказав той, хто дав <c>EntryRef</c> (<c>REGFIND</c>/<c>REGONE</c>).
    /// </remarks>
    private Typed CheckRegistryField(FunctionNode node, Walk walk, RegistryShape? row)
    {
        var arguments = node.Arguments.Select(a => Infer(a, walk, row)).ToList();

        if (arguments.Count != 2
            || !arguments[0].IsEntry
            || walk.Context.Registries is not { } registries
            || node.Arguments[1] is not LiteralNode { Type: ExpressionValueType.Text, Value: string path } literal
            || registries.FindRegistry(arguments[0].Registry!) is not { } shape)
        {
            return Typed.Of(ExpressionValueType.Null);
        }

        var segments = path.Split('.');
        var field = ReferenceResolver.ResolveRegistryPath(
            registries, shape, segments, i => LiteralSegmentSpan(literal.Position, segments, i), walk.Diagnostics);

        return field is null ? Typed.Of(ExpressionValueType.Null) : TypeOf(field);
    }

    /// <summary>
    /// <c>REGFIND(R, k1 …)</c>: довідник (15), кількість і типи частин ключа (17).
    /// </summary>
    private Typed CheckRegistryFind(FunctionNode node, Walk walk, RegistryShape? row)
    {
        var (code, shape) = RegistryArgument(node, walk, row);
        var parts = node.Arguments.Skip(1).Select(a => Infer(a, walk, row)).ToList();

        if (shape is not null)
        {
            CheckKey(node, shape, parts, walk.Diagnostics);
        }

        return code is null ? Typed.Of(ExpressionValueType.Number) : Typed.Entry(shape?.Code ?? code);
    }

    /// <summary>
    /// <c>REGONE(R, f)</c> і агрегати <c>REG*(R, f [, e])</c>: область рядка
    /// <c>ROW</c> для <c>f</c> і <c>e</c>, типи результату (§5.4) і попередження 21а.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>REGMIN</c>/<c>REGMAX</c> у каталозі мають тип <c>Null</c>: «число або
    /// дата» каталог сказати не може. Тут тип уточнюється за виразом <c>e</c> —
    /// саме те, що обіцяв каталог (<c>FunctionRegistry</c>, RT-20b).
    /// </remarks>
    private Typed CheckRegistryScan(FunctionNode node, string upper, Walk walk, RegistryShape? row)
    {
        var (code, shape) = RegistryArgument(node, walk, row);
        var arguments = node.Arguments;
        var diagnostics = walk.Diagnostics;

        // ⛔ Внутрішній `ROW` затіняє зовнішній (§5.2), навіть коли довідник
        // невідомий: тоді `ROW.` усередині нерезолвиться мовчки, а не чіпляється
        // до зовнішнього рядка.
        Typed? filter = arguments.Count > 1 ? Infer(arguments[1], walk, shape) : null;
        Typed? value = arguments.Count > 2 ? Infer(arguments[2], walk, shape) : null;

        if (filter is { } condition)
        {
            Require(condition.Type, ExpressionValueType.Boolean, arguments[1], diagnostics,
                "expr.type.conditionNeedsBoolean", null, "The condition must be Boolean.");
        }

        if (shape is not null && walk.Warnings is { } warnings && Aggregates.Contains(upper)
            && arguments.Count > 1 && !Indexed(arguments[1], shape))
        {
            warnings.Add(new ExpressionDiagnostic(
                string.Empty,
                $"The filter of {node.Name} on registry \"{shape.Code}\" has no indexed condition "
                + "(ROW.<Lookup field> = ...), so every row of the registry is scanned.",
                arguments[1].Position, 1,
                "expr.registryScanUnindexed",
                DiagnosticParams.Of(("function", node.Name), ("registry", shape.Code))));
        }

        switch (upper)
        {
            case RegistryForms.One:
                return code is null ? Typed.Of(ExpressionValueType.Number) : Typed.Entry(shape?.Code ?? code);

            case RegistryForms.Count:
                return Typed.Of(ExpressionValueType.Number);

            case RegistryForms.Sum:
            case RegistryForms.Average:
                if (value is { } summed)
                {
                    RequireNumeric(summed, arguments[2], diagnostics);
                }

                return Typed.Of(ExpressionValueType.Number);

            default:
                if (value is not { } extreme)
                {
                    return Typed.Of(ExpressionValueType.Null);
                }

                if (extreme.IsEntry)
                {
                    EntryRefMisuse(arguments[2], extreme, diagnostics);
                    return Typed.Of(ExpressionValueType.Null);
                }

                return extreme.Type is ExpressionValueType.Number or ExpressionValueType.Date
                    ? Typed.Of(extreme.Type)
                    : Typed.Of(ExpressionValueType.Null);
        }
    }

    /// <summary>
    /// Перший аргумент функції довідника: код — рядковий літерал, довідник існує
    /// (перевірка 15).
    /// </summary>
    /// <returns>Код (як написано) і форма; форма <c>null</c> — довідника немає або джерела форм немає.</returns>
    private (string? Code, RegistryShape? Shape) RegistryArgument(FunctionNode node, Walk walk, RegistryShape? row)
    {
        if (node.Arguments.Count == 0)
        {
            return (null, null);
        }

        var argument = node.Arguments[0];
        var code = ReferenceResolver.RegistryCodeLiteral(argument);

        if (code is null)
        {
            // Обчислений код знає лише рантайм — а тоді ні поля, ні ключ, ні
            // `RegistryUse` при публікації перевірити нема на чому.
            Infer(argument, walk, row);
            Report(walk.Diagnostics, argument,
                "expr.registryCodeMustBeLiteral", DiagnosticParams.Of(("function", node.Name)),
                $"The registry code of {node.Name} must be a string literal such as 'STREAM_CASE': "
                + "it is resolved when the formula is published.");
            return (null, null);
        }

        return walk.Context.Registries is { } registries
            ? (code, ReferenceResolver.ResolveRegistry(registries, code, argument, walk.Diagnostics))
            : (code, null);
    }

    /// <summary>Кількість і типи частин <c>REGFIND</c> проти первинного ключа (перевірка 17).</summary>
    /// <remarks>
    /// ⚠ Без первинного ключа <c>REGFIND</c> шукає за <c>Code</c> запису — одна
    /// текстова частина (§5.4).
    /// </remarks>
    private static void CheckKey(
        FunctionNode node, RegistryShape shape, IReadOnlyList<Typed> parts, List<ExpressionDiagnostic> diagnostics)
    {
        var key = shape.PrimaryKey;
        List<(string Field, Typed? Type)> expected = key is null
            ? [("Code", Typed.Of(ExpressionValueType.Text))]
            : [.. key.FieldCodes.Select(c => (c, shape.FindField(c) is { } f ? TypeOf(f) : (Typed?)null))];
        var fields = string.Join(", ", expected.Select(e => e.Field));

        if (parts.Count != expected.Count)
        {
            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.Syntax,
                $"REGFIND on registry \"{shape.Code}\" needs {expected.Count} key part(s) ({fields}), "
                + $"but {parts.Count} were given.",
                node.Position, node.Name.Length,
                "expr.registryKeyArity",
                DiagnosticParams.Of(
                    ("registry", shape.Code),
                    ("expected", expected.Count.ToString(CultureInfo.InvariantCulture)),
                    ("actual", parts.Count.ToString(CultureInfo.InvariantCulture)),
                    ("key", fields))));
            return;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            if (expected[i].Type is not { } wanted || Fits(parts[i], wanted))
            {
                continue;
            }

            var argument = node.Arguments[i + 1];
            diagnostics.Add(new ExpressionDiagnostic(
                ExpressionErrors.Syntax,
                $"Key part {i + 1} of REGFIND on registry \"{shape.Code}\" (field \"{expected[i].Field}\") "
                + $"must be {Describe(wanted)}, but it is {Describe(parts[i])}.",
                argument.Position, 1,
                "expr.registryKeyPartType",
                DiagnosticParams.Of(
                    ("registry", shape.Code),
                    ("index", (i + 1).ToString(CultureInfo.InvariantCulture)),
                    ("field", expected[i].Field),
                    ("expected", Describe(wanted)),
                    ("actual", Describe(parts[i])))));
        }
    }

    /// <summary>Чи підходить частина ключа до поля.</summary>
    /// <remarks>
    /// ⚠ <c>Null</c> підходить до всього (§6.2): невідомий статично тип — не
    /// твердження про невідповідність. <c>EntryRef</c> — лише того самого довідника:
    /// частина <c>Lookup</c> ключа — id цілі (§4.2, <c>L:162</c>), і id запису
    /// ІНШОГО довідника дав би «не знайдено» замість помилки.
    /// </remarks>
    private static bool Fits(Typed actual, Typed expected)
    {
        if (IsNull(actual))
        {
            return true;
        }

        if (expected.IsEntry || actual.IsEntry)
        {
            return expected.IsEntry && actual.IsEntry
                   && string.Equals(expected.Registry, actual.Registry, StringComparison.OrdinalIgnoreCase);
        }

        return expected.Type == ExpressionValueType.Null || actual.Type == expected.Type;
    }

    /// <summary>
    /// <c>ROW.a.b</c>: шлях від довідника області (перевірка 16) і тип поля.
    /// </summary>
    /// <remarks>
    /// ⚠ Позиція — усе посилання <c>ROW.…</c>: вузол знає лише, де починається
    /// <c>ROW</c>, а між сегментами дозволені пробіли, тож обчислена позиція
    /// сегмента могла б указувати не туди.
    /// </remarks>
    private static Typed InferRowField(RowFieldNode node, Walk walk, RegistryShape? row)
    {
        if (row is null || walk.Context.Registries is not { } registries)
        {
            return Typed.Of(ExpressionValueType.Null);
        }

        var length = "ROW".Length + node.Path.Sum(p => p.Length + 1);
        var field = ReferenceResolver.ResolveRegistryPath(
            registries, row, node.Path, _ => (node.Position, length), walk.Diagnostics);

        return field is null ? Typed.Of(ExpressionValueType.Null) : TypeOf(field);
    }

    /// <summary>
    /// Чи має фільтр індексний шлях — той самий, що бере обчислювач
    /// (<c>RegistryForms.Rows</c>): верхній кон'юнкт <c>ROW.&lt;Lookup&gt; = &lt;вираз без ROW&gt;</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Рівно те, що робить рантайм, і не більше: покриття ключа рівностями
    /// §5.4 згадує, але обчислювач RT-20b за ним не звужує перегляд. Не
    /// попередити там, де рантайм однаково перебирає все, означало б пообіцяти
    /// швидкість, якої немає.
    /// </remarks>
    private static bool Indexed(AstNode filter, RegistryShape shape)
    {
        var pending = new Stack<AstNode>();
        pending.Push(filter);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is BinaryNode { Operator: BinaryOperator.And } and)
            {
                pending.Push(and.Right);
                pending.Push(and.Left);
                continue;
            }

            if (node is not BinaryNode { Operator: BinaryOperator.Equal } equality)
            {
                continue;
            }

            var field = equality switch
            {
                { Left: RowFieldNode { Path.Count: 1 } left } when !ReadsRow(equality.Right) => left.Path[0],
                { Right: RowFieldNode { Path.Count: 1 } right } when !ReadsRow(equality.Left) => right.Path[0],
                _ => null,
            };

            if (field is not null && shape.FindField(field) is { DataType: CellDataType.Lookup })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Чи читає вираз рядок ПОТОЧНОЇ області (<c>ROW.</c> поза вкладеним агрегатом).</summary>
    /// <remarks>Та сама відповідь, що <c>RegistryForms.ReadsRow</c>; невідомий вузол — «читає».</remarks>
    private static bool ReadsRow(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case RowFieldNode:
                    return true;

                case LiteralNode or SymbolReferenceNode or PeriodPropertyNode or ThisNode:
                    break;

                case CellReferenceNode { Row: RowSelector.Predicate predicate }:
                    pending.Push(predicate.Condition);
                    break;

                case CellReferenceNode:
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.Condition);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;

                case FunctionNode function:
                    var own = RegistryForms.RowScopeNames.Contains(function.Name)
                        ? Math.Min(1, function.Arguments.Count)
                        : function.Arguments.Count;
                    for (var i = 0; i < own; i++)
                    {
                        pending.Push(function.Arguments[i]);
                    }

                    break;

                default:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Позиція сегмента <paramref name="index"/> усередині рядкового літерала шляху.</summary>
    /// <remarks>Літерал починається лапкою — звідси <c>+ 1</c>.</remarks>
    private static (int Position, int Length) LiteralSegmentSpan(int literalPosition, string[] segments, int index)
    {
        var offset = literalPosition + 1;
        for (var i = 0; i < index; i++)
        {
            offset += segments[i].Length + 1;
        }

        return (offset, segments[index].Length);
    }

    /// <summary>Статичний тип значення поля довідника (§5.3).</summary>
    private static Typed TypeOf(RegistryFieldShape field)
        => field.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal => Typed.Of(ExpressionValueType.Number),
            CellDataType.String or CellDataType.Unit => Typed.Of(ExpressionValueType.Text),
            CellDataType.Bool => Typed.Of(ExpressionValueType.Boolean),
            CellDataType.Date => Typed.Of(ExpressionValueType.Date),
            CellDataType.Lookup => field.LookupRegistryCode is { } target
                ? Typed.Entry(target)
                : Typed.Of(ExpressionValueType.Number),
            _ => Typed.Of(ExpressionValueType.Null),
        };

    private static string Describe(Typed type)
        => type.IsEntry ? $"EntryRef<{type.Registry}>" : type.Type.ToString();

    /// <summary>Чи можна порівнювати ці типи.</summary>
    private static bool Comparable(ExpressionValueType left, ExpressionValueType right)
        => left == ExpressionValueType.Null
           || right == ExpressionValueType.Null
           || left == right;

    private static bool Is(ExpressionValueType actual, ExpressionValueType expected)
        => actual == expected;

    private static bool IsNull(Typed type) => !type.IsEntry && type.Type == ExpressionValueType.Null;

    private static void RequireNumeric(Typed actual, AstNode node, List<ExpressionDiagnostic> diagnostics)
    {
        if (actual.IsEntry)
        {
            EntryRefMisuse(node, actual, diagnostics);
            return;
        }

        Require(actual.Type, ExpressionValueType.Number, node, diagnostics,
            "expr.type.arithmeticNeedsNumber", DiagnosticParams.Of(("actual", actual.Type.ToString())),
            $"Arithmetic expects a number, but the operand is of type {actual.Type}.");
    }

    /// <summary>Перевірка 19: <c>EntryRef</c> в арифметиці або в порівнянні з числом.</summary>
    private static void EntryRefMisuse(AstNode node, Typed entry, List<ExpressionDiagnostic> diagnostics)
        => Report(diagnostics, node,
            "expr.entryRefMisuse", DiagnosticParams.Of(("registry", entry.Registry ?? string.Empty)),
            $"A reference to an entry of registry \"{entry.Registry}\" cannot take part in arithmetic or be "
            + "compared with a number; compare it only with an entry of the same registry.");

    private static void Require(
        ExpressionValueType actual,
        ExpressionValueType expected,
        AstNode node,
        List<ExpressionDiagnostic> diagnostics,
        string messageKey,
        IReadOnlyDictionary<string, string>? messageParams,
        string message)
    {
        // Null сумісний з будь-чим: порожня комірка не робить формулу
        // неправильною, вона робить результат порожнім (02b §6.2).
        if (actual == expected || actual == ExpressionValueType.Null)
        {
            return;
        }

        Report(diagnostics, node, messageKey, messageParams, message);
    }

    private static void Report(
        List<ExpressionDiagnostic> diagnostics,
        AstNode node,
        string messageKey,
        IReadOnlyDictionary<string, string>? messageParams,
        string message)
        => diagnostics.Add(new ExpressionDiagnostic(
            ExpressionErrors.Unresolved, message, node.Position, 1, messageKey, messageParams));

    /// <summary>Стан одного обходу.</summary>
    private sealed record Walk(
        ITypeContext Context, List<ExpressionDiagnostic> Diagnostics, List<ExpressionDiagnostic>? Warnings);

    /// <summary>
    /// Статичний тип вузла: тип значення і, для <c>EntryRef</c>, довідник запису.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>EntryRef</c> — не новий член <see cref="ExpressionValueType"/>: у
    /// рантаймі це число (id запису, §5.3), і тип значення, який бачать решта
    /// рушія, клієнт і збережені формули, не змінюється. Різниця існує лише тут,
    /// при публікації, — рівно там, де її і треба ловити.
    /// </remarks>
    private readonly record struct Typed(ExpressionValueType Type, string? Registry)
    {
        public bool IsEntry => Registry is not null;

        public static Typed Of(ExpressionValueType type) => new(type, null);

        public static Typed Entry(string registry) => new(ExpressionValueType.Number, registry);
    }
}

/// <summary>
/// Що зв'язувачу треба знати про довідники (FEATURE-REGISTRY-TABLES §5.9) —
/// спільне для типів і одиниць.
/// </summary>
/// <remarks>
/// ⛔ Усі члени мають замовчування «нічого не знаю», і різниця не стилістична:
/// так само, як <see cref="ITypeContext.GetConstantType"/>, контекст без
/// реалізації мовчить, а не стверджує. Наявні контексти (версія шаблону,
/// звіти) до RT-23b/RT-24 форм довідників не мають — і не отримують хибних
/// відмов на формулах, які досі проходили.
///
/// ⚠ Ціна названа: контекст, що дає <see cref="Registries"/>, мусить дати й
/// цілі <c>Lookup</c> (<see cref="GetReferenceRegistry"/>,
/// <see cref="GetArgumentRegistry"/>). Інакше <c>Lookup</c>-колонка лишиться
/// <c>Text</c>, і <c>REGFIND('STREAM_CASE', [Stream], …)</c> отримає
/// <c>expr.registryKeyPartType</c> на законній частині ключа.
/// </remarks>
public interface IRegistryBindingContext
{
    /// <summary>Форми довідників; <c>null</c> — перевірки, яким потрібна форма, пропускаються.</summary>
    public IRegistryShapeSource? Registries => null;

    /// <summary>
    /// Довідник правила, у якому живе вираз (<c>THIS</c> і <c>ROW.</c> верхнього
    /// рівня, §6); <c>null</c> — вираз не є правилом довідника.
    /// </summary>
    public string? RuleRegistryCode => null;

    /// <summary>Ціль <c>Lookup</c>-колонки посилання; <c>null</c> — не <c>Lookup</c> або невідомо.</summary>
    /// <param name="reference">Посилання з дерева виразу.</param>
    public string? GetReferenceRegistry(CellReferenceNode reference) => null;

    /// <summary>Ціль аргументу <c>@Arg</c> з <c>Lookup</c>-колонки (лише <c>Strict</c>, §5.3).</summary>
    /// <param name="name">Ім'я аргументу без <c>@</c>.</param>
    public string? GetArgumentRegistry(string name) => null;

    /// <summary>
    /// Довідник запису, який дає інша формула (<c>!CASE = REGFIND(…)</c>);
    /// <c>null</c> — формула дає не <c>EntryRef</c> або невідомо.
    /// </summary>
    /// <param name="code">Код формули без <c>!</c>.</param>
    public string? GetFormulaRegistry(string code) => null;
}

/// <summary>Джерело типів для посилань.</summary>
public interface ITypeContext : IRegistryBindingContext
{
    /// <summary>Тип значення посилання з дерева виразу.</summary>
    /// <remarks>
    /// Приймає вузол цілком із тієї самої причини, що й
    /// <c>IEvaluationContext.Read</c>: у дереві посилання записане кодами, а
    /// резолвінг кодів потребує знімка метаданих.
    /// </remarks>
    public ExpressionValueType GetReferenceType(CellReferenceNode reference);

    /// <summary>Тип значення колонки.</summary>
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId);

    /// <summary>Тип аргументу методології.</summary>
    public ExpressionValueType GetArgumentType(string name);

    /// <summary>Тип значення константи методології (<c>CST.&lt;код&gt;</c>).</summary>
    /// <param name="code">Код константи — те, що стоїть після <c>CST.</c>.</param>
    /// <returns>Тип константи; <c>Null</c> — контекст про неї не знає.</returns>
    /// <remarks>
    /// ⛔ Реалізація за замовчуванням віддає <c>Null</c>, а не <c>Number</c>, і
    /// різниця не стилістична. <c>Null</c> сумісний з усім (§6.2), тобто
    /// контекст, який про константи нічого не знає, нічого про них і не
    /// стверджує. <c>Number</c> — це твердження, і саме воно перетворювало
    /// <c>CST.k1_CategorySelection_ = 'Summer'</c> на «порівняння різних
    /// типів»: ~90 хибних помилок публікації на законних текстових
    /// константах корпусу.
    ///
    /// ⚠ Ціна замовчування названа: контекст без реалізації не спіймає й
    /// СПРАВЖНЬОЇ несумісності — текстової константи в множенні. Її сьогодні
    /// ловить окрема перевірка публікації методології
    /// (<c>MethodologyPublishChecks</c>, позиційний обхід), і доки контекст
    /// методологій не реалізує цей метод, вона там і лишається єдиною.
    /// </remarks>
    public ExpressionValueType GetConstantType(string code) => ExpressionValueType.Null;
}
