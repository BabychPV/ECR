// src/Ecr.Application/Calculations/MethodologyCategoryRuleChecks.cs
using System.Globalization;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Calculations;

/// <summary>
/// Перевірки правила категорії константи при збереженні й публікації (L-2, <c>calc.CategoryRule</c>).
/// </summary>
/// <remarks>
/// ⛔ Правило — вираз діалекту Methodology над РЯДКОМ документа: воно бачить `@`, `!Row`-формули й
/// `CST.` без категорії (рушій обчислює його між Row-формулами і циклом речовин). Тому посилання на
/// формулу речовини в ньому — помилка, а не `#REF` у рантаймі на кожному рядку.
///
/// ⚠ Перевірка рантайму (<c>categoryRuleFailed</c>) від цієї не залежить: вона відмовляє РЯДКУ, ця —
/// не пускає в публікацію версію, де правило заздалегідь не може дати ключа.
/// </remarks>
public static class MethodologyCategoryRuleChecks
{
    /// <summary>Скільки кодів констант виводиться у попередженні, решта — «…».</summary>
    private const int ListedCodes = 10;

    /// <summary>Підсумок перевірки правила категорії версії.</summary>
    /// <param name="Problems">Проблеми, що блокують публікацію; порожньо — правило придатне.</param>
    /// <param name="Rule">
    /// Розібране правило як формула — для звірки його `@`-аргументів з колонками прив'язаних таблиць;
    /// <c>null</c> — правила немає або воно не розібралося.
    /// </param>
    public sealed record Outcome(IReadOnlyList<PublishProblem> Problems, ParsedFormula? Rule);

    /// <summary>Код, під яким правило виступає у звірці аргументів.</summary>
    public const string RuleCode = "CategoryRule";

    /// <summary>Перевіряє правило категорії версії й складає попередження про нього.</summary>
    /// <param name="ruleText">Вираз правила; <c>null</c> — правила немає.</param>
    /// <param name="engine">Парсер діалекту Methodology.</param>
    /// <param name="formulas">Формули версії (область — Row чи Substance).</param>
    /// <param name="parsed">Розібрані формули версії в тому ж складі.</param>
    /// <param name="constants">Усі константи версії.</param>
    /// <param name="warnings">Куди складати попередження; <c>null</c> — нікуди.</param>
    /// <param name="strings">Каталог рядків мовою читача; <c>null</c> — англійський запас.</param>
    /// <returns>Проблеми й розібране правило.</returns>
    public static Outcome Check(
        string? ruleText,
        IFormulaEngine engine,
        IReadOnlyList<MethodologyFormula> formulas,
        IReadOnlyList<ParsedFormula> parsed,
        IReadOnlyList<MethodologyConstant> constants,
        ICollection<string>? warnings,
        UiStringCatalog? strings)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(constants);

        var categorised = Categorised(constants);

        if (string.IsNullOrWhiteSpace(ruleText))
        {
            // ⚠ Попередження, не відмова: так версію публікували й до L-2, а константа з кількома
            // категоріями без правила дає `constantAmbiguous` лише на прогоні, коли рядок уже подано.
            if (categorised.Count > 0)
            {
                Warn(
                    warnings,
                    strings,
                    "publish.warning.categoryRuleMissing",
                    "Constants with more than one category ({count}): {constants}. The version has no category rule, "
                    + "so each of them is ambiguous at calculation time.",
                    ("count", categorised.Count.ToString(CultureInfo.InvariantCulture)),
                    ("constants", List(categorised)));
            }

            return new Outcome([], null);
        }

        var result = engine.Parse(ruleText, ExpressionDialect.Methodology);
        if (!result.IsSuccess || result.Expression is null)
        {
            var reason = result.Diagnostics.Count > 0 ? result.Diagnostics[0].Message : "unparseable expression";

            return new Outcome(
                [PublishProblem.Of(
                    "publish.problem.categoryRuleInvalid",
                    $"Правило категорії не розбирається: {reason}",
                    ("reason", reason))],
                null);
        }

        var problems = new List<PublishProblem>();
        var known = constants.Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var scopes = formulas.ToDictionary(f => f.Code, f => f.Scope, StringComparer.OrdinalIgnoreCase);

        foreach (var node in Nodes(result.Expression.Root))
        {
            if (node is SymbolReferenceNode { Kind: SymbolKind.Constant } constant && !known.Contains(constant.Name))
            {
                problems.Add(PublishProblem.Of(
                    "publish.problem.categoryRuleUnknownConstant",
                    $"Правило категорії посилається на константу «CST.{constant.Name}», якої у версії немає.",
                    ("code", constant.Name)));
            }
            else if (node is SymbolReferenceNode { Kind: SymbolKind.Formula } formula
                     && !(scopes.TryGetValue(formula.Name, out var scope) && scope == MethodologyFormulaScope.Row))
            {
                // Формула речовини (або невідома) у Row-контексті правила — #REF на кожному рядку.
                problems.Add(PublishProblem.Of(
                    "publish.problem.categoryRuleBadFormula",
                    $"Правило категорії посилається на «!{formula.Name}»: правило бачить лише Row-формули "
                    + "версії (воно рахується раз на рядок, до циклу речовин).",
                    ("formula", formula.Name)));
            }
        }

        // Ключ категорії — текст: число, булеве чи дата ніколи не збіжаться з `Category`.
        if (result.Expression.ResultType is ExpressionValueType.Number or ExpressionValueType.Boolean
            or ExpressionValueType.Date)
        {
            problems.Add(PublishProblem.Of(
                "publish.problem.categoryRuleNotText",
                "Правило категорії повертає не текст: ключ категорії — текст (наприклад, 'Diesel').",
                ("type", result.Expression.ResultType.ToString())));
        }

        // ⚠ Row-формула, що читає категорійну константу, лишається БЕЗ категорії: ключ дає правило
        // ПІСЛЯ Row-фази. Методолог мусить перевести таку формулу в область речовини.
        var rowRoots = parsed
            .Where(p => p.Root is not null && scopes.TryGetValue(p.Code, out var s) && s == MethodologyFormulaScope.Row)
            .ToList();

        foreach (var row in rowRoots)
        {
            foreach (var code in Nodes(row.Root!)
                         .OfType<SymbolReferenceNode>()
                         .Where(n => n.Kind == SymbolKind.Constant)
                         .Select(n => n.Name)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(c => categorised.Contains(c, StringComparer.OrdinalIgnoreCase)))
            {
                Warn(
                    warnings,
                    strings,
                    "publish.warning.categoryRuleRowConstant",
                    "Row formula {formula} reads constant {constant}, which has several categories: the Row phase "
                    + "runs before the category rule, so the constant is ambiguous there. Make the formula a substance formula.",
                    ("formula", row.Code),
                    ("constant", code));
            }
        }

        return new Outcome(problems, new ParsedFormula(RuleCode, FormulaResultType.Text, result.Expression.Root));
    }

    /// <summary>Коди констант, що мають ≥ 2 різні категорії (без «Common» і без категорії).</summary>
    private static List<string> Categorised(IReadOnlyList<MethodologyConstant> constants)
        => [.. constants
            .Where(c => !string.IsNullOrEmpty(c.Category)
                        && !string.Equals(c.Category, MethodologyConstant.CommonCategory, StringComparison.Ordinal))
            .GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(c => c.Category).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => g.Key)
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static string List(List<string> codes)
        => string.Join(", ", codes.Take(ListedCodes)) + (codes.Count > ListedCodes ? ", …" : string.Empty);

    /// <summary>Усі вузли дерева, обхід явним стеком (L7-01).</summary>
    private static IEnumerable<AstNode> Nodes(AstNode root)
    {
        var pending = new Stack<AstNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            switch (node)
            {
                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case ConditionalNode conditional:
                    pending.Push(conditional.WhenFalse);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.Condition);
                    break;

                case FunctionNode function:
                    for (var i = function.Arguments.Count - 1; i >= 0; i--)
                    {
                        pending.Push(function.Arguments[i]);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static void Warn(
        ICollection<string>? warnings,
        UiStringCatalog? strings,
        string key,
        string fallback,
        params (string Name, string Value)[] args)
    {
        if (warnings is null)
        {
            return;
        }

        var values = args.ToDictionary(a => a.Name, a => a.Value, StringComparer.Ordinal);

        warnings.Add(strings is null
            ? values.Aggregate(fallback, (text, pair) => text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.Ordinal))
            : UiStringResolver.Format(UiStringResolver.Resolve(strings, key), values));
    }
}
