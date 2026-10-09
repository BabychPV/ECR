// src/Ecr.Application/Calculations/LegacyIntegerMaxMinChecks.cs
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Calculations;

/// <summary>
/// Попередження публікації <c>Legacy</c>-методології: <c>Max</c>/<c>Min</c> із цілим
/// першим аргументом і можливо дробовим другим.
/// </summary>
/// <remarks>
/// ⛔ C1-01 (аудит 2026-10-09c), HU-14 Q1 — за замовчуванням ECR рахує математично
/// (<c>double</c>), семантика <c>Max</c>/<c>Min</c>/<c>Abs</c>/<c>Sign</c> НЕ змінюється.
/// Але NCalc 1.3.8 (<c>Numbers.Max</c>) бере тип ПЕРШОГО аргументу: при цілому
/// першому другий зводиться <c>Convert.ToInt32</c> (банківське округлення), тож
/// <c>Max(0, 12.7)</c> у чинній системі — <c>13</c>, а в ECR — <c>12.7</c>. Різниця —
/// цілі одиниці, без жодної помилки. Попередження дає методологу побачити такі
/// формули до звірки з поданими формами.
///
/// ⚠ «Цілий» — тип NCalc, виведений статично: числовий літерал без дробової частини
/// (лексер не має експоненти), <c>Sign(…)</c> (повертає <c>Int32</c>), <c>Max</c>/<c>Min</c>
/// з цілим першим, унарний знак і <c>+ - * / %</c> над двома цілими, а <c>if</c>/<c>? :</c>
/// — якщо ціла хоча б одна гілка (тип там залежить від умови, тож попередження
/// обережне). Аргументи <c>@X</c> і константи цілими НЕ вважаються: чинна збірка
/// подає параметри <c>double</c> (<c>Utilities.cs:188-215</c>), тож «ціле поле» в
/// NCalc цілого типу не дає.
/// </remarks>
public static class LegacyIntegerMaxMinChecks
{
    /// <summary>Ключ каталогу тексту попередження.</summary>
    public const string WarningKey = "publish.warning.legacyIntegerMaxMin";

    /// <summary>Додає попередження для кожної формули з таким викликом.</summary>
    /// <param name="parsed">Розібрані формули версії.</param>
    /// <param name="mode">Арифметичний режим версії; для <c>Strict</c> нічого не робить.</param>
    /// <param name="warnings">Список попереджень публікації.</param>
    /// <param name="strings">Каталог мовою того, хто публікує; <c>null</c> — англійський запас.</param>
    public static void Warn(
        IEnumerable<ParsedFormula> parsed,
        NumericMode mode,
        ICollection<string> warnings,
        UiStringCatalog? strings)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(warnings);

        if (mode != NumericMode.Legacy)
        {
            return;
        }

        foreach (var formula in parsed)
        {
            if (formula.Root is null)
            {
                continue;
            }

            var functions = Nodes(formula.Root)
                .OfType<FunctionNode>()
                .Where(IsRoundingMaxMin)
                .Select(call => call.Name)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            if (functions.Count == 0)
            {
                continue;
            }

            var args = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["formula"] = formula.Code,
                ["functions"] = string.Join(", ", functions),
            };

            warnings.Add(strings is null
                ? $"Formula \"{formula.Code}\": {args["functions"]} has an integer first argument and a possibly "
                  + "fractional second one. The legacy system (NCalc 1.3.8) rounded the second argument to an "
                  + "integer here, ECR does not: results may differ from the submitted forms by whole units. "
                  + "Write the first argument with a decimal point (0.0) to make the intent explicit."
                : UiStringResolver.Format(UiStringResolver.Resolve(strings, WarningKey), args));
        }
    }

    /// <summary>Виклик, у якому NCalc округлив би другий аргумент до цілого.</summary>
    private static bool IsRoundingMaxMin(FunctionNode call)
        => IsMaxMin(call) && IsInteger(call.Arguments[0]) && !IsInteger(call.Arguments[1]);

    private static bool IsMaxMin(FunctionNode call)
        => call.Arguments.Count == 2
           && (string.Equals(call.Name, "Max", StringComparison.Ordinal)
               || string.Equals(call.Name, "Min", StringComparison.Ordinal));

    /// <summary>Чи може вузол мати в NCalc 1.3.8 цілий тип (див. зауваження класу).</summary>
    private static bool IsInteger(AstNode node)
        => node switch
        {
            LiteralNode { Type: ExpressionValueType.Number, Value: decimal number } => number.Scale == 0,
            UnaryNode { Operator: UnaryOperator.Negate or UnaryOperator.Plus } unary => IsInteger(unary.Operand),
            BinaryNode
            {
                Operator: BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
                    or BinaryOperator.Divide or BinaryOperator.Modulo,
            } binary => IsInteger(binary.Left) && IsInteger(binary.Right),
            ConditionalNode conditional => IsInteger(conditional.WhenTrue) || IsInteger(conditional.WhenFalse),
            FunctionNode { Name: "Sign" } => true,
            FunctionNode { Name: "if", Arguments.Count: 3 } call
                => IsInteger(call.Arguments[1]) || IsInteger(call.Arguments[2]),
            FunctionNode call when IsMaxMin(call) => IsInteger(call.Arguments[0]),
            _ => false,
        };

    private static IEnumerable<AstNode> Nodes(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            foreach (var child in Children(node))
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<AstNode> Children(AstNode node)
        => node switch
        {
            UnaryNode unary => [unary.Operand],
            BinaryNode binary => [binary.Left, binary.Right],
            ConditionalNode conditional => [conditional.Condition, conditional.WhenTrue, conditional.WhenFalse],
            FunctionNode function => function.Arguments,
            _ => [],
        };
}
