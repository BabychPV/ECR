using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Зміна бізнес-ключа документа (ФВ-3.9): «контрольована операція з аудитом, не
/// редагування поля». Право <c>Document.ChangeKey</c> + грант Write на проєкт.
/// </summary>
/// <remarks>
/// Посилання на документ у схемі — за <c>Id</c>, тож ключ оновлюється в одному місці;
/// історія старих ключів — у <c>aud.SecurityEvent</c> (<c>DocumentKeyChanged</c>).
/// Конкурентність — через очікуваний старий ключ у тілі: <c>DocumentSummary</c>
/// не несе <c>rowVersion</c>, а ключ і є тим, що бачила людина.
/// </remarks>
public sealed class ChangeDocumentKeyHandler(
    IDocumentKeyStore keys,
    IDocumentDeletionStore workflowFacts,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну ключа.</summary>
    public const string Permission = "Document.ChangeKey";

    /// <summary>Тип події журналу безпеки.</summary>
    public const string EventType = "DocumentKeyChanged";

    /// <summary>Довжина колонки <c>doc.Document.BusinessKey</c>.</summary>
    public const int MaxKeyLength = 200;

    /// <summary>Найдовша причина.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Змінює ключ, якщо чинний ключ досі <paramref name="expectedKey"/>.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-DOC-0422</c> — немає причини чи ключ невалідний.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-DOC-0409</c> — ключ зайнятий або застарів.</exception>
    /// <exception cref="DomainException"><c>ECR-DOC-0409</c> — аркуш поданий чи погоджений.</exception>
    public async Task HandleAsync(
        long documentId, string? newKey, string? expectedKey, string? reason, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var key = newKey?.Trim() ?? string.Empty;
        var why = reason?.Trim() ?? string.Empty;
        if (why.Length == 0 || why.Length > MaxReasonLength)
        {
            throw Invalid("Зміна ключа потребує причини.", "err.ECR-DOC-0422.rekeyReasonRequired");
        }

        if (key.Length == 0 || key.Length > MaxKeyLength)
        {
            throw Invalid($"Новий ключ — від 1 до {MaxKeyLength} символів.", "err.ECR-DOC-0422.rekeyKeyInvalid");
        }

        string oldKey = string.Empty;
        int projectId = 0;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var document = await keys.FindForUpdateAsync(documentId, innerCt).ConfigureAwait(false);
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

            if (!string.Equals(document.BusinessKey, expectedKey, StringComparison.Ordinal))
            {
                throw Conflict(
                    $"Ключ документа {documentId} змінили після того, як його прочитали.",
                    "err.ECR-DOC-0409.rekeyStale", document.BusinessKey);
            }

            if (string.Equals(document.BusinessKey, key, StringComparison.Ordinal))
            {
                throw Invalid("Новий ключ збігається з чинним.", "err.ECR-DOC-0422.rekeyKeyInvalid");
            }

            var facts = await workflowFacts.LockWorkflowFactsAsync(documentId, innerCt).ConfigureAwait(false);
            DocumentKeyChange.EnsureChangeable(facts.SheetStates);

            if (await keys.IsKeyTakenAsync(document.ProjectId, key, documentId, innerCt).ConfigureAwait(false))
            {
                throw Conflict($"Ключ «{key}» уже має інший документ проєкту.", "err.ECR-DOC-0409.rekeyDuplicate", key);
            }

            oldKey = document.BusinessKey;
            projectId = document.ProjectId;
            document.ChangeBusinessKey(key, profile.UserId, clock.UtcNow);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        // Журнал — ПІСЛЯ коміту, як у DeleteDocumentHandler: IAuditWriter пише власним підключенням.
        await audit.WriteSecurityEventAsync(
            new SecurityEventRecord(
                clock.UtcNow, EventType, TargetUserId: null, TargetRoleId: null,
                JsonSerializer.Serialize(new { documentId, projectId, oldKey, newKey = key, reason = why }),
                profile.UserId, currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }

    private static BusinessRuleException Invalid(string message, string messageKey)
        => new(ErrorCodes.DocumentCompositionInvalid, message,
               new Dictionary<string, object?> { ["messageKey"] = messageKey, ["maxLength"] = MaxKeyLength });

    private static ConcurrencyConflictException Conflict(string message, string messageKey, string businessKey)
        => new(ErrorCodes.DocumentSubmitted, message,
               new Dictionary<string, object?> { ["messageKey"] = messageKey, ["businessKey"] = businessKey });
}
