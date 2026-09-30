using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;

namespace Ecr.Application.Templates;

/// <summary>Публікаційна перевірка ВИРАЗІВ правил валідації (V-19).</summary>
/// <remarks>
/// ⛔ До V-19 публікація дивилася на правила лише як на структуру
/// (<see cref="PublishChecks.CheckRules"/>: суперечливі рівні, покриття
/// обов'язкових колонок), а їхні вирази не розбирала ніде. Правило
/// <c>[CDEC] &gt;=</c>, збережене до того, як збереження почало відмовляти,
/// публікувалося й доїжджало до документа, де <c>ValidationEngine</c> мовчки
/// його пропускав. Той самий <see cref="PublishChecks.CheckExpression"/>, що
/// й для формул, — з тими самими ключами зауважень.
/// </remarks>
public static class RuleExpressionChecks
{
    /// <summary>Ключ каталогу: посилання, яке контекст правила не підтримує (ФВ-5.9).</summary>
    public const string UnsupportedReferenceKey = "expr.ruleReferenceUnsupported";

    /// <summary>Зауваження до виразів активних правил живих таблиць.</summary>
    /// <param name="version">Версія, що публікується.</param>
    /// <param name="formulaEngine">Рушій — розбір і резолвінг.</param>
    public static IReadOnlyList<ExpressionDiagnostic> Check(TemplateVersion version, IFormulaEngine formulaEngine)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var diagnostics = new List<ExpressionDiagnostic>();
        var scope = new ExpressionScope(formulaEngine, PublishChecks.Snapshot(version), null, null);

        foreach (var table in PublishChecks.LiveTables(version.Sheets))
        {
            foreach (var rule in table.ValidationRules.Where(r => r.IsActive))
            {
                var checkedRule = PublishChecks.CheckExpression(
                    rule.Expression,
                    ExpressionDialect.Template,
                    new ExpressionSite(table.Id, null, rule.ColumnDefId),
                    scope,
                    diagnostics);

                // ⛔ ФВ-5.9: те, чого контекст правила не вміє, публікація відхиляє
                // явно, а не пускає в рантайм, де воно читається мовчки не тим.
                if (checkedRule is not null)
                {
                    CheckSupportedReferences(checkedRule.Expression.Root, diagnostics);
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Відмовляє в збереженні правила з посиланням, якого контекст правила не підтримує
    /// (<c>ФВ-5.9</c>): інша таблиця чи аркуш, інший період.
    /// </summary>
    /// <param name="formulaEngine">Рушій — розбір.</param>
    /// <param name="expression">Текст виразу правила.</param>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-TMPL-0422</c> із ключем <see cref="UnsupportedReferenceKey"/> і переліком зауважень.
    /// </exception>
    public static void RequireSupportedReferences(IFormulaEngine formulaEngine, string expression)
    {
        ArgumentNullException.ThrowIfNull(formulaEngine);

        var parsed = formulaEngine.Parse(expression, ExpressionDialect.Template);
        if (parsed.Expression is null)
        {
            return;
        }

        var diagnostics = new List<ExpressionDiagnostic>();
        CheckSupportedReferences(parsed.Expression.Root, diagnostics);

        if (diagnostics.Count > 0)
        {
            throw ExpressionRejection.Build(
                diagnostics,
                "err.ECR-TMPL-0422.expressionInvalid",
                $"Вираз «{expression}» відхилено: {diagnostics[0].Message}",
                extra: new Dictionary<string, object?> { ["expression"] = expression });
        }
    }

    /// <summary>
    /// Зауваження до посилань, які контекст правила читає НЕ ТИМ, чим вони є.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>ValidationEngine</c> віддає значення лише СВОГО документа й СВОЄЇ
    /// таблиці: <c>ScopeContext.Read</c> ігнорує і таблицю, і аркуш, і зсув
    /// періоду, беручи колонку поточної таблиці за поточний період. Правило
    /// <c>[Water].[R].[Jan] &gt; 0</c> порівнювало б тому <c>Jan</c> поточної
    /// таблиці — і не давало б жодної помилки. Константи (<c>CST.X</c>) і
    /// параметри діалект шаблону відхиляє ще парсером.
    /// </remarks>
    private static void CheckSupportedReferences(AstNode node, List<ExpressionDiagnostic> diagnostics)
    {
        switch (node)
        {
            case CellReferenceNode cell:
                if (cell.SheetCode is not null || cell.TableCode is not null || cell.PeriodOffset != 0)
                {
                    var reference = Describe(cell);
                    diagnostics.Add(new ExpressionDiagnostic(
                        ExpressionErrors.Unresolved,
                        $"Правило валідації бачить лише свою таблицю за поточний період: посилання {reference} "
                        + "на іншу таблицю, аркуш чи період не підтримується.",
                        cell.Position,
                        1,
                        UnsupportedReferenceKey,
                        new Dictionary<string, string> { ["reference"] = reference }));
                }

                if (cell.Row is RowSelector.Predicate predicate)
                {
                    CheckSupportedReferences(predicate.Condition, diagnostics);
                }

                break;

            case BinaryNode binary:
                CheckSupportedReferences(binary.Left, diagnostics);
                CheckSupportedReferences(binary.Right, diagnostics);
                break;

            case UnaryNode unary:
                CheckSupportedReferences(unary.Operand, diagnostics);
                break;

            case ConditionalNode conditional:
                CheckSupportedReferences(conditional.Condition, diagnostics);
                CheckSupportedReferences(conditional.WhenTrue, diagnostics);
                CheckSupportedReferences(conditional.WhenFalse, diagnostics);
                break;

            case FunctionNode function:
                foreach (var argument in function.Arguments)
                {
                    CheckSupportedReferences(argument, diagnostics);
                }

                break;

            case PeriodPropertyNode { PeriodOffset: not 0 } period:
                diagnostics.Add(new ExpressionDiagnostic(
                    ExpressionErrors.Unresolved,
                    "Правило валідації бачить лише поточний період: календарний контекст іншого періоду "
                    + "не підтримується.",
                    period.Position,
                    1,
                    UnsupportedReferenceKey,
                    new Dictionary<string, string>
                    {
                        ["reference"] = $"[Period:{period.PeriodOffset.ToString(CultureInfo.InvariantCulture)}].{period.Property}",
                    }));
                break;
        }
    }

    /// <summary>Посилання в записі виразу: <c>[Period:-1].[Sheet].[Table].[Row].[Col]</c>.</summary>
    private static string Describe(CellReferenceNode cell)
    {
        var parts = new List<string>();

        if (cell.PeriodOffset != 0)
        {
            parts.Add($"[Period:{cell.PeriodOffset.ToString(CultureInfo.InvariantCulture)}]");
        }

        if (cell.SheetCode is not null)
        {
            parts.Add($"[{cell.SheetCode}]");
        }

        if (cell.TableCode is not null)
        {
            parts.Add($"[{cell.TableCode}]");
        }

        parts.Add($"[{cell.ColumnSelector}]");

        return string.Join('.', parts);
    }
}
