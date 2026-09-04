// src/Ecr.Application/Documents/CreateDocumentHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Documents;

/// <summary>Створює документ і його склад аркушів (ФВ-3.1, ФВ-3.2).</summary>
/// <remarks>
/// Документ **наскрізний по періодах**: період живе на рядках і комірках, а не
/// тут. Унікальність — `(ProjectId, BusinessKey)`.
/// </remarks>
public sealed class CreateDocumentHandler(
    IMetadataCache metadata,
    IDocumentStore documents,
    IUnitOfWork uow,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на створення документа (`02-contracts.md` §9).</summary>
    public const string Permission = "Document.Create";

    /// <summary>Створює документ із заданим складом аркушів.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateVersionId">Версія шаблону.</param>
    /// <param name="sheetDefIds">Аркуші, які входять у документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатор документа.</returns>
    public async Task<long> HandleAsync(
        int projectId, int templateVersionId, IReadOnlyList<int> sheetDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheetDefIds);

        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може створювати документи.");

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var known = snapshot.Sheets.Select(s => s.Id).ToHashSet();

        var unknown = sheetDefIds.Where(id => !known.Contains(id)).ToList();
        if (unknown.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-DOC-0422",
                "Склад документа містить аркуші, яких немає у версії шаблону.",
                new Dictionary<string, object?> { ["sheetDefIds"] = unknown });
        }

        // ⚠ Склад перевіряється за SheetGroupRule: RequiresAll / RequiresOne /
        // Optional (ФВ-3.2). Порушення — ECR-DOC-0422, і саме тут, а не при
        // поданні: документ із неповним складом виглядав би готовим, а не
        // виявився б непридатним у момент здачі.
        var violations = await documents
            .ValidateCompositionAsync(templateVersionId, sheetDefIds, ct).ConfigureAwait(false);

        if (violations.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-DOC-0422",
                "Склад документа порушує правила груп аркушів.",
                new Dictionary<string, object?> { ["violations"] = violations });
        }

        // BusinessKey складається зі значень колонок IsBusinessKey. До появи
        // даних значень ще немає, тому ключ будується з проєкту і версії —
        // унікальність у межах проєкту тримає індекс, а не домовленість.
        var businessKey = await documents
            .NextBusinessKeyAsync(projectId, templateVersionId, ct).ConfigureAwait(false);

        var now = clock.UtcNow;
        var document = new Document(projectId, businessKey, userId, now);

        // ⛔ Статус документа НЕ ставиться — його не існує (D-93). Робочий стан
        // з'явиться у wf.ApprovalState при першому Submit, і буде він на
        // аркуш × період, а не на документ цілком.
        foreach (var sheetDefId in sheetDefIds.Distinct())
        {
            document.IncludeSheet(sheetDefId);
        }

        // ⚠ TableInstance створюються ЛІНИВО, при першому записі в період, а
        // не одразу на всі дванадцять: більшість документів заповнюють не всі
        // періоди, і дванадцятикратна порожня структура коштувала б місця й
        // часу на кожному зрізі.
        await documents.AddAsync(document, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        return document.Id;
    }
}
