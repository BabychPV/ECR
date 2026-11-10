// src/Ecr.Application/Templates/ColumnUsageHandler.cs
using System.Globalization;
using System.Runtime.CompilerServices;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Templates;

/// <summary>«Де використовується» колонка шаблону (ФВ-8.14).</summary>
/// <remarks>
/// Право — <c>Template.View</c>, як у пошуку колонок: перелік нічого не змінює,
/// а питання «що зламається, якщо я її чіпну» ставить і той, хто лише дивиться.
/// <para>
/// ⚠ Правила валідації залежностей у <c>cfg.FormulaDependency</c> не мають, тож їхні вирази розбираються тим
/// самим <see cref="IFormulaEngine"/>, що й на збереженні (як у <c>ConstantUsageHandler</c>): пошук підрядком
/// зарахував би колонці код, що стоїть у текстовому літералі, або колонку <c>C10</c> як <c>C1</c>.
/// </para>
/// </remarks>
public sealed class ColumnUsageHandler(
    IWhereUsedStore store,
    IFormulaEngine formulaEngine,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Перші <see cref="UsageResponse.PageSize"/> посилань і загальна кількість.</summary>
    /// <param name="columnDefId">Колонка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Колонки немає або її видалено — <c>ECR-TMPL-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(int columnDefId, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, SearchColumnDefsHandler.Permission, ct)
            .ConfigureAwait(false);

        if (!await store.ColumnExistsAsync(columnDefId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-TMPL-0404",
                $"Колонки {columnDefId} не існує або її видалено.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.column",
                    ["columnDefId"] = columnDefId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var usage = await store.FindColumnUsageAsync(columnDefId, UsageResponse.PageSize, ct).ConfigureAwait(false);

        var rules = (await store.ListColumnRuleCandidatesAsync(columnDefId, ct).ConfigureAwait(false))
            .Where(c => c.BoundToColumn || ReadsColumn(c.Expression, c.ColumnCode))
            .Select(c => c.Item)
            .ToList();

        if (rules.Count == 0)
        {
            return usage;
        }

        var items = usage.Items.Concat(rules.Take(Math.Max(0, UsageResponse.PageSize - usage.Items.Count))).ToList();
        return new UsageResponse(usage.Total + rules.Count, items);
    }

    /// <summary>Чи читає вираз правила колонку <paramref name="columnCode"/> (точний збіг коду, без регістру).</summary>
    private bool ReadsColumn(string expression, string columnCode)
    {
        var parsed = formulaEngine.Parse(expression, ExpressionDialect.Template);
        return parsed.Expression is not null && References(parsed.Expression.Root, columnCode);
    }

    private static bool References(AstNode node, string code)
    {
        // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
        RuntimeHelpers.EnsureSufficientExecutionStack();

        return node switch
        {
            // Правило бачить лише свою таблицю (ФВ-5.9): посилання з іншою таблицею чи аркушем — не на цю колонку.
            CellReferenceNode cell => (cell.SheetCode is null && cell.TableCode is null
                                       && string.Equals(cell.ColumnSelector, code, StringComparison.OrdinalIgnoreCase))
                                      || (cell.Row is RowSelector.Predicate predicate && References(predicate.Condition, code)),
            UnaryNode unary => References(unary.Operand, code),
            BinaryNode binary => References(binary.Left, code) || References(binary.Right, code),
            ConditionalNode conditional => References(conditional.Condition, code)
                || References(conditional.WhenTrue, code)
                || References(conditional.WhenFalse, code),
            FunctionNode function => function.Arguments.Any(a => References(a, code)),
            _ => false,
        };
    }
}
