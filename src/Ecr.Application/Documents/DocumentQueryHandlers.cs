using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents;

/// <summary>Перелік документів. Право <c>Document.View</c>.</summary>
public sealed class ListDocumentsHandler(
    IDocumentStore documents, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду документів.</summary>
    public const string Permission = "Document.View";

    /// <summary>Повертає сторінку документів, видимих користувачу.</summary>
    /// <param name="projectId">Фільтр за проєктом; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період для зведеного стану; <c>null</c> — без стану.</param>
    /// <param name="page">Курсорна пагінація.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<DocumentSummary>> HandleAsync(
        int? projectId, int? periodKey, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var profile = await ProfileAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                "ECR-CELL-0422", $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.");
        }

        var all = await documents
            .ListAsync(projectId, new PeriodKeyFilter(periodKey), page, ct)
            .ConfigureAwait(false);

        // ⚠ Фільтр за грантами обов'язковий: перелік документів чужого
        // проєкту — це вже відомості про те, які об'єкти звітують і як часто.
        var visible = all.Items
            .Where(d => profile.LevelFor(ResourceKind.Project, d.ProjectId) >= GrantLevel.Read)
            .ToList();

        return new PagedResult<DocumentSummary>(visible, all.NextCursor, all.TotalCount);
    }

    /// <summary>Профіль користувача з перевіркою функціонального права.</summary>
    internal static async Task<AccessProfile> ProfileAsync(
        IAccessDecisionService access, ICurrentUser currentUser, string permission, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Потрібна автентифікація.");

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        if (!profile.Has(permission))
        {
            throw new AccessDeniedException("ECR-AUTH-0403", $"Потрібне право {permission}.");
        }

        return profile;
    }
}

/// <summary>Документ і стан його аркушів. Право <c>Document.View</c>.</summary>
public sealed class GetDocumentHandler(
    IDocumentStore documents, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Повертає документ; <c>null</c> — не існує або невидимий.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період для стану аркушів.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<DocumentSummary?> HandleAsync(
        long documentId, int? periodKey, CancellationToken ct)
    {
        var profile = await ListDocumentsHandler
            .ProfileAsync(access, currentUser, ListDocumentsHandler.Permission, ct)
            .ConfigureAwait(false);

        var document = await documents
            .FindAsync(documentId, new PeriodKeyFilter(periodKey), ct)
            .ConfigureAwait(false);

        // ⚠ Документ без гранта віддається як «не знайдено», а не «заборонено».
        // Різниця між 403 і 404 тут сама по собі є відомістю: за нею видно,
        // які документи існують у проєктах, доступу до яких немає.
        return document is null
               || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read
            ? null
            : document;
    }
}
