// src/Ecr.Application/Calculations/MethodologyCategoryHandlers.cs
using System.Globalization;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;

namespace Ecr.Application.Calculations;

/// <summary>Перелік категорій констант версії з кількістю констант під кожною (план CONST-CATEGORY).</summary>
public sealed class ListMethodologyCategoriesHandler(
    IMethodologyDraftStore drafts,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання — як і константи версії (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.View";

    /// <summary>Читає перелік.</summary>
    /// <param name="methodologyVersionId">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Категорії в порядку переліку.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    public async Task<IReadOnlyList<MethodologyCategoryDto>> HandleAsync(int methodologyVersionId, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        _ = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

        var categories = await drafts.GetCategoriesTrackedAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var usage = await drafts.CountConstantsByCategoryAsync(methodologyVersionId, ct).ConfigureAwait(false);

        return [.. categories.Select(c => MethodologyCategoryChecks.Map(c, usage))];
    }
}

/// <summary>
/// Заводить категорію в переліку <b>чернетки</b> або змінює її опис, порядок і стан (план CONST-CATEGORY).
/// </summary>
/// <remarks>
/// ⛔ Код — адреса і ключ зіставлення: його не перейменовують. Той самий код в іншому регістрі —
/// <c>409 categoryRename</c>, а не тихе «оновлення»: резолвер порівнює категорії з регістром, і зміна регістру
/// зробила б мертвими всі константи під старим кодом.
/// <para>
/// ⚠ Право — як і на константи (<c>Calculation.EditConstant</c>): перелік обмежує саме запис констант.
/// </para>
/// </remarks>
public sealed class SaveMethodologyCategoryHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditConstant";

    /// <summary>Записує категорію; створює її, якщо коду ще немає.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код категорії з маршруту.</param>
    /// <param name="description">Опис; <c>null</c> — немає.</param>
    /// <param name="ordinal">Порядок; <c>null</c> — нова в кінець, наявна не зсувається.</param>
    /// <param name="isRetired">Вивести з ужитку; <c>null</c> — не змінювати (нова — в ужитку).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записана категорія з кількістю констант.</returns>
    /// <exception cref="NotFoundException">Версії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c> — недопустимий код або задовгий опис; <c>ECR-CALC-0409</c> — версія опублікована,
    /// код існує в іншому регістрі або категорію, на яку посилаються константи, виводять з ужитку.
    /// </exception>
    public async Task<MethodologyCategoryDto> HandleAsync(
        int methodologyVersionId,
        string code,
        string? description,
        int? ordinal,
        bool? isRetired,
        CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        MethodologyCategoryChecks.RequireValidCode(code);
        if (description is { Length: > MethodologyCategory.MaxDescriptionLength })
        {
            throw MethodologyCategoryChecks.Invalid(code);
        }

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

        var categories = await drafts.GetCategoriesTrackedAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var usage = await drafts.CountConstantsByCategoryAsync(methodologyVersionId, ct).ConfigureAwait(false);

        var existing = categories.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !string.Equals(existing.Code, code, StringComparison.Ordinal))
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Категорія «{existing.Code}» уже є у версії {methodologyVersionId}; «{code}» відрізняється лише регістром. "
                + "Код категорії не перейменовують: заведіть нову й перенесіть константи.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.categoryRename",
                    ["category"] = code,
                    ["existing"] = existing.Code,
                });
        }

        MethodologyCategory category;
        if (existing is null)
        {
            category = version.AddCategory(code, ordinal ?? (categories.Count == 0 ? 1 : categories.Max(c => c.Ordinal) + 1));
            drafts.Add(category);
        }
        else
        {
            category = existing;
        }

        var retire = isRetired ?? category.IsRetired;
        if (retire && !category.IsRetired)
        {
            MethodologyCategoryChecks.RequireUnused(category.Code, usage);
        }

        version.EditCategory(category, description, ordinal ?? category.Ordinal, retire);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return MethodologyCategoryChecks.Map(category, usage);
    }
}

/// <summary>Прибирає категорію з переліку <b>чернетки</b>, якщо на неї не посилається жодна константа.</summary>
public sealed class DeleteMethodologyCategoryHandler(
    IMethodologyDraftStore drafts,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на редагування (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.EditConstant";

    /// <summary>Видаляє категорію.</summary>
    /// <param name="methodologyVersionId">Версія-чернетка.</param>
    /// <param name="code">Код категорії (точно, з регістром).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Версії або категорії немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0409</c> — версія опублікована або на категорію посилаються константи (<c>categoryInUse</c>).
    /// </exception>
    public async Task HandleAsync(int methodologyVersionId, string code, CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var version = await drafts.FindVersionAsync(methodologyVersionId, ct).ConfigureAwait(false)
            ?? throw SaveMethodologyCategoryRuleHandler.VersionNotFound(methodologyVersionId);

        var categories = await drafts.GetCategoriesTrackedAsync(methodologyVersionId, ct).ConfigureAwait(false);
        var category = categories.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal))
            ?? throw new NotFoundException(
                "ECR-CALC-0404",
                $"Категорії «{code}» у версії {methodologyVersionId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0404.category",
                    ["category"] = code,
                    ["methodologyVersionId"] = methodologyVersionId.ToString(CultureInfo.InvariantCulture),
                });

        version.RemoveCategory(category);

        var usage = await drafts.CountConstantsByCategoryAsync(methodologyVersionId, ct).ConfigureAwait(false);
        MethodologyCategoryChecks.RequireUnused(category.Code, usage);

        drafts.Remove(category);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Спільні правила переліку категорій: код, зайнятість, вибір категорії константою.</summary>
public static class MethodologyCategoryChecks
{
    /// <summary>DTO категорії з кількістю констант.</summary>
    /// <param name="category">Категорія.</param>
    /// <param name="usage">Категорія → кількість констант.</param>
    /// <returns>DTO.</returns>
    public static MethodologyCategoryDto Map(MethodologyCategory category, IReadOnlyDictionary<string, int> usage)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(usage);

        return new MethodologyCategoryDto(
            category.Id,
            category.Code,
            category.Description,
            category.Ordinal,
            category.IsRetired,
            usage.GetValueOrDefault(category.Code));
    }

    /// <summary>Код категорії: непорожній, без пробілів по краях, не довший за межу колонки.</summary>
    /// <param name="code">Код.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0422</c> <c>categoryCodeInvalid</c>.</exception>
    /// <remarks>
    /// ⚠ Пробіли ВСЕРЕДИНІ дозволені (<c>6585 (001)</c> — так у корпусі AF); по краях — ні: правило категорії
    /// таке значення не поверне, і категорія була б мертвою від народження.
    /// </remarks>
    public static void RequireValidCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)
            || code.Length > MethodologyCategory.MaxCodeLength
            || !string.Equals(code, code.Trim(), StringComparison.Ordinal))
        {
            throw Invalid(code);
        }
    }

    /// <summary>Відмова «недопустимий код або опис категорії».</summary>
    /// <param name="code">Код із запиту.</param>
    /// <returns>Виняток <c>ECR-CALC-0422</c>.</returns>
    internal static BusinessRuleException Invalid(string? code)
        => new(
            "ECR-CALC-0422",
            $"Категорія «{code}»: код — від 1 до {MethodologyCategory.MaxCodeLength} символів без пробілів по краях, "
            + $"опис — до {MethodologyCategory.MaxDescriptionLength}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0422.categoryCodeInvalid",
                ["category"] = code,
                ["maxLength"] = MethodologyCategory.MaxCodeLength.ToString(CultureInfo.InvariantCulture),
            });

    /// <summary>Категорію, на яку посилаються константи, не прибирають і не виводять з ужитку.</summary>
    /// <param name="code">Код категорії.</param>
    /// <param name="usage">Категорія → кількість констант.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-CALC-0409</c> <c>categoryInUse</c>.</exception>
    public static void RequireUnused(string code, IReadOnlyDictionary<string, int> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var count = usage.GetValueOrDefault(code);
        if (count > 0)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                $"Категорія «{code}» — ключ зіставлення {count} констант(и): її не прибирають і не виводять з ужитку, "
                + "доки константи не перенесено в іншу категорію.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-CALC-0409.categoryInUse",
                    ["category"] = code,
                    ["count"] = count.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    /// <summary>
    /// Категорія константи — з переліку версії (точний код, не виведена з ужитку) або порожня (спільна).
    /// </summary>
    /// <param name="constantCode">Код константи.</param>
    /// <param name="category">Категорія із запиту; <c>null</c> — спільна.</param>
    /// <param name="current">Категорія, що вже стоїть на константі (її не перевіряють повторно).</param>
    /// <param name="categories">Перелік версії.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-0422</c> — <c>constantCategoryUnknown</c> або <c>constantCategoryRetired</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Порівняння ПОРЯДКОВЕ, з регістром — як у резолвері (<c>ConstantResolver</c>): <c>diesel</c> при
    /// <c>Diesel</c> у переліку — відмова, а не тихо мертва константа.
    /// </remarks>
    public static void RequireListed(
        string constantCode, string? category, string? current, IReadOnlyList<MethodologyCategory> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);

        if (category is null || string.Equals(category, current, StringComparison.Ordinal))
        {
            return;
        }

        var listed = categories.FirstOrDefault(c => string.Equals(c.Code, category, StringComparison.Ordinal));
        if (listed is null || listed.IsRetired)
        {
            var key = listed is null
                ? "err.ECR-CALC-0422.constantCategoryUnknown"
                : "err.ECR-CALC-0422.constantCategoryRetired";
            throw new BusinessRuleException(
                "ECR-CALC-0422",
                listed is null
                    ? $"Константа «{constantCode}»: категорії «{category}» немає в переліку версії — додайте її в перелік або оберіть наявну."
                    : $"Константа «{constantCode}»: категорію «{category}» виведено з ужитку.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = key,
                    ["constantCode"] = constantCode,
                    ["category"] = category,
                });
        }
    }
}
