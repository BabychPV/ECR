using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.MethodologyScriptMigration.Conversion;

/// <summary>Результат перекладу виразу.</summary>
/// <param name="Text">Вираз у синтаксисі «ECR Script».</param>
/// <param name="Formulas">Посилання <c>!F</c> у порядку появи (імена як у джерелі).</param>
/// <param name="ParsedInImportMode">Вираз прочитано в режимі імпорту (голі імена параметрів).</param>
public sealed record TranslatedExpression(string Text, IReadOnlyList<string> Formulas, bool ParsedInImportMode);

/// <summary>
/// Переклад виразу діалекту методологій у «ECR Script» (план, розд. 8.2): розбирає наявним <see cref="Parser"/>
/// і друкує AST у синтаксисі скрипта з дужками за пріоритетами граматики 3.3.
/// </summary>
/// <remarks>
/// ⚠ <c>if(c, a, b)</c> друкується тернарним оператором, а <c>in(x, a, b)</c> — як <c>x in (a, b)</c>: <c>if</c> і
/// <c>in</c> у скрипті — ключові слова, викликом їх записати не можна; обидві форми опускаються в той самий вузол AST
/// (<c>FunctionNode("if"|"in")</c>, розд. 3.5), тобто семантика та сама.
/// </remarks>
public sealed class ExpressionTranslator
{
    private const int PrecTernary = 1;
    private const int PrecOr = 2;
    private const int PrecAnd = 3;
    private const int PrecEquality = 4;
    private const int PrecRelational = 5;
    private const int PrecAdditive = 6;
    private const int PrecMultiplicative = 7;
    private const int PrecUnary = 8;
    private const int PrecAtom = 9;

    private static readonly Parser SharedParser = new();

    private readonly Func<string, string> _constant;
    private readonly Func<string, string> _formula;
    private readonly bool _keyArguments;
    private readonly bool _moduleLevel;
    private readonly Func<string, bool>? _isOptional;

    /// <summary>Створює перекладач.</summary>
    /// <param name="constant">Код константи → вираз її читання у скрипті.</param>
    /// <param name="formula">Ім'я формули (<c>!F</c>) → вираз у скрипті (<c>F</c> або <c>Lib.F</c>).</param>
    /// <param name="keyArguments">Правило категорії: <c>@X</c> → <c>key(row.X)</c>.</param>
    /// <param name="moduleLevel">Рівень <c>Module</c>: <c>@GCV.x_y_z</c> → <c>row.GCV_x_y_z</c> (крапка стає підкресленням —
    /// модуль місячних колонок віддає обидва імені, а скрипт читає лише друге).</param>
    /// <param name="isOptional">
    /// Чи ім'я аргументу необов'язкове для версії (старий рушій читає це зі списків <c>ArgumentsCsv</c> усіх формул версії й
    /// бібліотек, а не з маркера в самому виразі); <c>null</c> — вірити маркеру <c>@X?</c> у виразі.
    /// </param>
    public ExpressionTranslator(
        Func<string, string> constant, Func<string, string> formula, bool keyArguments = false, bool moduleLevel = false,
        Func<string, bool>? isOptional = null)
    {
        ArgumentNullException.ThrowIfNull(constant);
        ArgumentNullException.ThrowIfNull(formula);
        _constant = constant;
        _formula = formula;
        _keyArguments = keyArguments;
        _moduleLevel = moduleLevel;
        _isOptional = isOptional;
    }

    /// <summary>Перекладає вираз.</summary>
    /// <param name="expression">Старий текст виразу.</param>
    /// <exception cref="ConversionException">Вираз не розібрано або містить непереносиму конструкцію.</exception>
    public TranslatedExpression Translate(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);

        var importMode = false;
        var parsed = SharedParser.Parse(expression, ExpressionDialect.Methodology, ExpressionParseMode.Editor);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            var fallback = SharedParser.Parse(expression, ExpressionDialect.Methodology, ExpressionParseMode.Import);
            if (fallback.IsSuccess && fallback.Expression is not null)
            {
                parsed = fallback;
                importMode = true;
            }
            else
            {
                var message = parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : "unparseable expression";
                throw new ConversionException("expressionParse", $"Expression «{expression}» does not parse: {message}");
            }
        }

        var formulas = new List<string>();
        var printed = Print(parsed.Expression.Root, formulas);
        return new TranslatedExpression(printed.Text, formulas, importMode);
    }

    private readonly record struct Printed(string Text, int Prec);

    private Printed Print(AstNode node, List<string> formulas)
    {
        switch (node)
        {
            case LiteralNode literal:
                return PrintLiteral(literal);

            case UnaryNode unary:
            {
                var operand = Print(unary.Operand, formulas);
                var needsParens = operand.Prec < PrecUnary
                                  || unary.Operand is UnaryNode
                                  || (unary.Operand is LiteralNode { Type: ExpressionValueType.Number } lit
                                      && (decimal)lit.Value! < 0);
                var op = unary.Operator switch
                {
                    UnaryOperator.Negate => "-",
                    UnaryOperator.Plus => "+",
                    _ => "!",
                };
                return new Printed(op + (needsParens ? "(" + operand.Text + ")" : operand.Text), PrecUnary);
            }

            case BinaryNode binary:
                return PrintBinary(binary, formulas);

            case ConditionalNode conditional:
                return PrintTernary(
                    Print(conditional.Condition, formulas),
                    Print(conditional.WhenTrue, formulas),
                    Print(conditional.WhenFalse, formulas));

            case FunctionNode function:
                return PrintFunction(function, formulas);

            case SymbolReferenceNode symbol:
                return PrintSymbol(symbol, formulas);

            case PeriodPropertyNode period:
                return PrintPeriod(period);

            default:
                throw new ConversionException(
                    "unsupportedNode", $"Expression construct {node.GetType().Name} is not available in the methodology dialect.");
        }
    }

    private static Printed PrintLiteral(LiteralNode literal)
    {
        switch (literal.Type)
        {
            case ExpressionValueType.Number:
            {
                var value = literal.Value switch
                {
                    decimal d => d,
                    double dbl => (decimal)dbl,
                    _ => throw new ConversionException("unsupportedNode", "Numeric literal of an unexpected CLR type."),
                };
                var text = value.ToString(CultureInfo.InvariantCulture);
                return new Printed(text, value < 0 ? PrecUnary : PrecAtom);
            }

            case ExpressionValueType.Text:
                return new Printed(ScriptText.Quote((string)literal.Value!), PrecAtom);
            case ExpressionValueType.Boolean:
                return new Printed((bool)literal.Value! ? "true" : "false", PrecAtom);
            case ExpressionValueType.Null:
                return new Printed("null", PrecAtom);
            default:
                throw new ConversionException("unsupportedLiteral", $"Literal of type {literal.Type} has no script form.");
        }
    }

    private Printed PrintBinary(BinaryNode binary, List<string> formulas)
    {
        var (op, prec) = binary.Operator switch
        {
            BinaryOperator.Add => ("+", PrecAdditive),
            BinaryOperator.Subtract => ("-", PrecAdditive),
            BinaryOperator.Multiply => ("*", PrecMultiplicative),
            BinaryOperator.Divide => ("/", PrecMultiplicative),
            BinaryOperator.Modulo => ("%", PrecMultiplicative),
            BinaryOperator.Equal => ("==", PrecEquality),
            BinaryOperator.NotEqual => ("!=", PrecEquality),
            BinaryOperator.Less => ("<", PrecRelational),
            BinaryOperator.LessOrEqual => ("<=", PrecRelational),
            BinaryOperator.Greater => (">", PrecRelational),
            BinaryOperator.GreaterOrEqual => (">=", PrecRelational),
            BinaryOperator.And => ("&&", PrecAnd),
            BinaryOperator.Or => ("||", PrecOr),
            _ => throw new ConversionException(
                "unsupportedOperator", $"Operator {binary.Operator} has no script form (power is Pow(a, b))."),
        };

        var left = Print(binary.Left, formulas);
        var right = Print(binary.Right, formulas);

        // Порівняння (<, <=, >, >=) не ланцюжаться: обидва операнди мусять бути строго вищого рівня.
        var leftNeeds = prec == PrecRelational ? left.Prec <= prec : left.Prec < prec;
        var rightNeeds = right.Prec <= prec;
        var text = (leftNeeds ? "(" + left.Text + ")" : left.Text)
                   + " " + op + " "
                   + (rightNeeds ? "(" + right.Text + ")" : right.Text);
        return new Printed(text, prec);
    }

    private static Printed PrintTernary(Printed condition, Printed whenTrue, Printed whenFalse)
    {
        var c = condition.Prec <= PrecTernary ? "(" + condition.Text + ")" : condition.Text;
        var t = whenTrue.Prec <= PrecTernary ? "(" + whenTrue.Text + ")" : whenTrue.Text;
        return new Printed(c + " ? " + t + " : " + whenFalse.Text, PrecTernary);
    }

    private Printed PrintFunction(FunctionNode function, List<string> formulas)
    {
        var args = function.Arguments.Select(a => Print(a, formulas)).ToList();

        if (function.Name == "if")
        {
            if (args.Count != 3)
            {
                throw new ConversionException("functionArity", $"if() takes 3 arguments, got {args.Count}.");
            }

            return PrintTernary(args[0], args[1], args[2]);
        }

        if (function.Name == "in")
        {
            if (args.Count < 2)
            {
                throw new ConversionException("functionArity", "in() takes a value and at least one candidate.");
            }

            var subject = args[0].Prec <= PrecRelational ? "(" + args[0].Text + ")" : args[0].Text;
            return new Printed(
                subject + " in (" + string.Join(", ", args.Skip(1).Select(a => a.Text)) + ")", PrecRelational);
        }

        if (!ScriptText.IsName(function.Name))
        {
            throw new ConversionException("invalidName", $"Function name «{function.Name}» cannot be written in a script.");
        }

        return new Printed(function.Name + "(" + string.Join(", ", args.Select(a => a.Text)) + ")", PrecAtom);
    }

    private Printed PrintSymbol(SymbolReferenceNode symbol, List<string> formulas)
    {
        switch (symbol.Kind)
        {
            case SymbolKind.Argument:
            {
                var argument = _moduleLevel ? symbol.Name.Replace('.', '_') : symbol.Name;
                if (!ScriptText.IsName(argument))
                {
                    throw new ConversionException(
                        "invalidName", $"Argument «{symbol.Name}» is not a valid column name for row.<name>.");
                }

                var optional = _isOptional is null ? symbol.IsOptional : _isOptional(symbol.Name);
                var field = "row." + argument + (optional ? "?" : string.Empty);
                return new Printed(_keyArguments ? "key(" + field + ")" : field, PrecAtom);
            }

            case SymbolKind.Constant:
                return new Printed(_constant(symbol.Name), PrecAtom);

            case SymbolKind.Formula:
                formulas.Add(symbol.Name);
                return new Printed(_formula(symbol.Name), PrecAtom);

            default:
                if (!ScriptText.IsName(symbol.Name))
                {
                    throw new ConversionException("invalidName", $"Header name «{symbol.Name}» cannot be written as header.<name>.");
                }

                return new Printed("header." + symbol.Name, PrecAtom);
        }
    }

    private static Printed PrintPeriod(PeriodPropertyNode period)
    {
        if (period.PeriodOffset != 0)
        {
            throw new ConversionException("unsupportedNode", "The calendar context of another period is not supported.");
        }

        var name = period.Property.ToUpperInvariant() switch
        {
            "DAYS" => "Days",
            "HOURS" => "Hours",
            "SECONDS" => "Seconds",
            "START" => "Start",
            "END" => "End",
            "YEAR" => "Year",
            "SEQUENCE" => "Month",
            _ => throw new ConversionException("unsupportedPeriodProperty", $"[Period].{period.Property} has no script form."),
        };
        return new Printed("period." + name, PrecAtom);
    }
}
