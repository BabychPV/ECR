// src/Ecr.Application/Calculations/ConstantUsageHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Calculations;

/// <summary>«Де використовується» константа методики (ФВ-8.14).</summary>
/// <remarks>
/// ⚠ Константа живе у версії, і посилатися на неї (<c>CST.код</c>) можуть лише
/// формули ТІЄЇ Ж версії: діалект шаблону <c>CST.</c> відхиляє в парсері, а
/// <c>cfg.FormulaDependency</c> констант не зберігає. Тому вирази
/// розбираються тим самим <see cref="IFormulaEngine"/>, що й на публікації, —
/// пошук підрядком зарахував би <c>CST.K10</c> як вживання <c>K1</c>.
/// </remarks>
public sealed class ConstantUsageHandler(
    IWhereUsedStore store,
    IFormulaEngine formulaEngine,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Перелік формул версії, що посилаються на константу.</summary>
    /// <param name="methodologyId">Методологія з маршруту.</param>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="code">Код константи — те, що стоїть після <c>CST.</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Немає версії в цій методології або константи — <c>ECR-CALC-0404</c>.</exception>
    public async Task<UsageResponse> HandleAsync(
        int methodologyId, int methodologyVersionId, string code, CancellationToken ct)
    {
        await Security.PermissionCheck
            .RequireAsync(access, currentUser, ListMethodologyConstantsHandler.Permission, ct)
            .ConfigureAwait(false);

        var owner = await store.FindConstantMethodologyAsync(methodologyVersionId, code, ct).ConfigureAwait(false);
        if (owner != methodologyId)
        {
            throw new NotFoundException(
                "ECR-CALC-0404",
                $"У версії {methodologyVersionId} методології {methodologyId} немає константи «{code}».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.constant",
                    ["code"] = code,
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var route = "/admin/methodologies/" + methodologyId.ToString(CultureInfo.InvariantCulture) + "/versions";
        var hits = new List<UsageItemDto>();

        foreach (var formula in await store.ListVersionFormulasAsync(methodologyVersionId, ct).ConfigureAwait(false))
        {
            var parsed = formulaEngine.Parse(formula.Expression, ExpressionDialect.Methodology);
            if (parsed.Expression is not null && References(parsed.Expression.Root, code))
            {
                hits.Add(new UsageItemDto(
                    UsageKinds.MethodologyFormula,
                    formula.Id.ToString(CultureInfo.InvariantCulture),
                    formula.Code,
                    route));
            }
        }

        return new UsageResponse(hits.Count, [.. hits.Take(UsageResponse.PageSize)]);
    }

    /// <summary>Чи є у дереві <c>CST.code</c> — точний збіг імені, не префікс.</summary>
    private static bool References(AstNode node, string code)
        => node switch
        {
            SymbolReferenceNode { Kind: SymbolKind.Constant } symbol
                => string.Equals(symbol.Name, code, StringComparison.OrdinalIgnoreCase),
            UnaryNode unary => References(unary.Operand, code),
            BinaryNode binary => References(binary.Left, code) || References(binary.Right, code),
            ConditionalNode conditional => References(conditional.Condition, code)
                || References(conditional.WhenTrue, code)
                || References(conditional.WhenFalse, code),
            FunctionNode function => function.Arguments.Any(a => References(a, code)),
            _ => false,
        };
}
