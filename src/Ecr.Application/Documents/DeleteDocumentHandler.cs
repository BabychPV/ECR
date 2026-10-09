using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents;

/// <summary>
/// Видалення документа-чернетки. Право <c>Document.Delete</c> + грант на запис у проєкт.
/// </summary>
/// <remarks>
/// Рішення людини 2026-09-21: «лише чернетки». Що таке чернетка — вирішує домен
/// (<see cref="DraftDocumentDeletion"/>), обробник лише збирає факти під блокуванням.
/// </remarks>
public sealed class DeleteDocumentHandler(
    IDocumentStore documents,
    IDocumentDeletionStore deletion,
    ISheetEditGate structureGate,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на видалення документа.</summary>
    public const string Permission = "Document.Delete";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string DeletedEventType = "DocumentDeleted";

    /// <summary>Видаляє документ-чернетку.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-DOC-0404</c> — немає або не видно.</exception>
    /// <exception cref="DomainException"><c>ECR-DOC-0409</c> — документ не чернетка.</exception>
    public async Task HandleAsync(long documentId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може видаляти документи.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        // Чужий документ = неіснуючий: 403 сам по собі видав би, що такий документ є.
        var document = await documents.FindAsync(documentId, new PeriodKeyFilter(null), ct).ConfigureAwait(false);
        if (document is null || profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Read)
        {
            throw new NotFoundException(
                "ECR-DOC-0404", $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ ФВ-6.14: право — у проєкті ЦЬОГО документа.
        PermissionCheck.RequireIn(profile, Permission, document.ProjectId);

        if (profile.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {document.ProjectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = document.ProjectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var cells = 0;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ X8-05 (R6): структура документа — ВИНЯТКОВО й першою дією, як у переносу версії
            // (`MigrateDocumentVersionHandler`); той самий порядок `doc-structure` → решта, що в усіх
            // писарів. Доти видалення брало лише `wf.ApprovalState`, а правка комірок, рядок, шапка
            // його не беруть: видалення йшло паралельно із записом, а запис, що ввійшов у транзакцію
            // вже після коміту видалення, падав на FK 547 → 500. Тепер запис у транзакції або
            // завершується до видалення (і видалення забирає його дані разом із рештою), або чекає
            // й бачить, що документа немає (`SheetEditGate` → 404).
            if (await structureGate.EnterStructureAsync(documentId, exclusive: true, innerCt).ConfigureAwait(false) is null)
            {
                // Паралельне видалення того самого документа вже зафіксувалося.
                throw new NotFoundException(
                    "ECR-DOC-0404", $"Документ {documentId} не знайдено.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["messageKey"] = "err.ECR-DOC-0404.document",
                        ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                    });
            }

            var facts = await deletion.LockWorkflowFactsAsync(documentId, innerCt).ConfigureAwait(false);
            // ⛔ Відмова не називає код і стан аркуша, якого читач не бачить: схований непорожній аркуш
            // зводиться до загальної причини «документ уже проходив погодження» (без sheetDefId/reason).
            var hidden = await DocumentSheetVisibility
                .HiddenStatesAsync(access, profile, documentId, facts.SheetStates, innerCt).ConfigureAwait(false);
            DraftDocumentDeletion.EnsureDraft(
                [.. facts.SheetStates.Except(hidden)],
                facts.HasWorkflowHistory || hidden.Any(s => s.Status != DocumentStatus.Draft));
            cells = await deletion.DeleteAsync(documentId, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Журнал — ПІСЛЯ коміту: `IAuditWriter` пише власним підключенням, і запис
        // до коміту лишив би слід видалення, якого не сталося (як у SetTemplateArchivedHandler).
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow,
                DeletedEventType,
                TargetUserId: null,
                TargetRoleId: null,
                JsonSerializer.Serialize(new
                {
                    documentId,
                    projectId = document.ProjectId,
                    businessKey = document.BusinessKey,
                    cells,
                }),
                userId,
                currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }
}
