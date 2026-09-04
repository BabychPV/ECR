using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;

namespace Ecr.Application.Templates;

/// <summary>Перелік шаблонів. Право <c>Template.View</c>.</summary>
public sealed class ListTemplatesHandler(
    ITemplateVersionStore templates, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду шаблонів.</summary>
    public const string Permission = "Template.View";

    /// <summary>Повертає сторінку шаблонів.</summary>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<TemplateSummary>> HandleAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                "ECR-CELL-0422", $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.");
        }

        return await templates.ListTemplatesAsync(page, ct).ConfigureAwait(false);
    }

    /// <summary>Перевіряє функціональне право поточного користувача.</summary>
    /// <remarks>
    /// ⚠ Право перевіряється через <c>IAccessDecisionService</c>, а не
    /// атрибутом із назвою ролі: перевірка ролі поза єдиною точкою рішення
    /// заборонена архітектурним правилом 7. Атрибут бачить лише ім'я політики,
    /// а доступ у ECR залежить від ресурсу.
    /// </remarks>
    internal static async Task RequireAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {permission}.");
        }
    }
}

/// <summary>Створення шаблону. Право <c>Template.Edit</c>.</summary>
public sealed class CreateTemplateHandler(
    ITemplateVersionStore templates,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право редагування шаблонів.</summary>
    public const string Permission = "Template.Edit";

    /// <summary>Створює шаблон.</summary>
    /// <param name="code">Код шаблону, унікальний у системі.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<int> HandleAsync(
        string code, IReadOnlyDictionary<string, string> name, CancellationToken ct)
    {
        await ListTemplatesHandler.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var templateId = await templates
            .CreateTemplateAsync(code, name, currentUser.UserId!.Value, clock.UtcNow, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return templateId;
    }
}

/// <summary>Версії шаблону. Право <c>Template.View</c>.</summary>
public sealed class ListTemplateVersionsHandler(
    ITemplateVersionStore templates, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає версії шаблону.</summary>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<TemplateVersionSummary>> HandleAsync(
        int templateId, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, ListTemplatesHandler.Permission, ct)
            .ConfigureAwait(false);

        return await templates.ListVersionsAsync(templateId, page, ct).ConfigureAwait(false);
    }
}
