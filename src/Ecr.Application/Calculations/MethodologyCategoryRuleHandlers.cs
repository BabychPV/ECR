// src/Ecr.Application/Calculations/MethodologyCategoryRuleHandlers.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;

namespace Ecr.Application.Calculations;

/// <summary>
/// Правило категорії константи версії (L-2, <c>calc.CategoryRule</c>): читання.
/// </summary>
/// <remarks>
/// ⚠ Правила може не бути — це законний стан (версія без категорій), і відповідь тоді 200 з
/// <c>expression = null</c>, а не 404: 404 лишається для версії, якої немає.
/// </remarks>
public sealed class GetMethodologyCategoryRuleHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає правило категорії версії.</summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Правило; <c>Expression = null</c>, якщо його немає.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<MethodologyCategoryRuleDto> HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

        var rule = await drafts.FindCategoryRuleAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return SaveMethodologyCategoryRuleHandler.Map(rule);
    }
}

/// <summary>
/// Заводить або переписує правило категорії константи **чернетки** (L-2).
/// </summary>
/// <remarks>
/// ⛔ Вираз перевіряється ДО запису: не розбирається — <c>categoryRuleInvalid</c>, дає число —
/// <c>categoryRuleNotText</c>. Посилання на константи й формули версії перевіряє ПУБЛІКАЦІЯ
/// (<c>MethodologyCategoryRuleChecks</c>): чернетка може заводитись у довільному порядку, і вимагати
/// тут уже готових формул означало б змусити методолога писати правило останнім.
///
/// ⚠ Право те саме, що й на правила відбору рядків (<c>Calculation.EditRule</c>): це теж частина
/// того, ЩО і з чим рахується рядок, і окремої ролі під неї немає.
/// </remarks>
public sealed class SaveMethodologyCategoryRuleHandler(
    IMethodologyDraftStore drafts,
    IFormulaEngine formulaEngine,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на редагування (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditRule";

    /// <summary>Записує правило категорії версії.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="expression">Вираз діалекту Methodology; результат — текст (ключ категорії).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записане правило.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c> — порожній, нерозібраний або нетекстовий вираз; <c>ECR-CALC-0409</c> —
    /// версія опублікована.
    /// </exception>
    public async Task<MethodologyCategoryRuleDto> HandleAsync(
        int methodologyVersionId, string expression, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw VersionNotFound(methodologyVersionId);

        RequireUsable(expression);

        var existing = await drafts.FindCategoryRuleAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var rule = version.SetCategoryRule(existing, expression, clock.UtcNow);

        if (existing is null)
        {
            drafts.Add(rule);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return Map(rule);
    }

    /// <summary>Правило як DTO; <c>null</c> — правила немає.</summary>
    /// <param name="rule">Правило версії.</param>
    /// <returns>DTO для конфігуратора.</returns>
    internal static MethodologyCategoryRuleDto Map(MethodologyCategoryRule? rule)
        => new(rule?.Expression, rule?.UpdatedAt);

    /// <summary>Відмова «версії немає».</summary>
    /// <param name="methodologyVersionId">Версія з маршруту.</param>
    /// <returns>Виняток 404 <c>ECR-CALC-0404</c>.</returns>
    internal static NotFoundException VersionNotFound(int methodologyVersionId)
        => new(
            "ECR-CALC-0404",
            $"Версії методології {methodologyVersionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0404.version",
                ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Вираз мусить розбиратись діалектом Methodology і не повертати число.</summary>
    private void RequireUsable(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Правило категорії без виразу: «правила немає» записується видаленням правила.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.categoryRuleEmpty" });
        }

        var parsed = formulaEngine.Parse(expression, ExpressionDialect.Methodology);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            var reason = parsed.Diagnostics.Count > 0 ? parsed.Diagnostics[0].Message : "unparseable expression";

            throw new BusinessRuleException(
                "ECR-CALC-0422",
                $"Правило категорії не розбирається: {reason}",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0422.categoryRuleInvalid",
                    ["reason"] = reason,
                });
        }

        if (parsed.Expression.ResultType is ExpressionValueType.Number or ExpressionValueType.Boolean
            or ExpressionValueType.Date)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                "Правило категорії повертає не текст: ключ категорії — текст (наприклад, 'Diesel').",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-CALC-0422.categoryRuleNotText" });
        }
    }
}

/// <summary>Прибирає правило категорії **чернетки** (L-2): версія знову резолвить константи без категорії.</summary>
/// <remarks>
/// ⚠ Ідемпотентно: правила й так немає — це не помилка (повторний DELETE клієнта після обриву).
/// </remarks>
public sealed class DeleteMethodologyCategoryRuleHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditRule";

    /// <summary>Видаляє правило категорії версії.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0409</c> — версія опублікована.</exception>
    public async Task HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

        var rule = await drafts.FindCategoryRuleAsync(methodologyVersionId, ct).ConfigureAwait(false);
        if (rule is null)
        {
            return;
        }

        version.RemoveCategoryRule(rule);
        drafts.Remove(rule);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
