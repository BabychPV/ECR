// src/Ecr.Application/Documents/EditAssignments/TransferDocumentOwnerHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>
/// Передача власності документа (D-443, AN-130): власник (<c>Document.CreatedByUserId</c>) або менеджер проєкту
/// призначає нового власника; подія <see cref="TransferredEvent"/> у журналі безпеки з обома ідентифікаторами.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Хто:</b> той самий, хто керує учасниками (<see cref="EditAssignmentAccess.CanManage(AccessProfile, EditAssignmentDocumentRef)"/>):
/// документ видимий, рівень проєкту ≥ <c>Write</c> і (власник ∨ <c>Manage</c>), не симуляція, проєкт не в архіві.</item>
/// <item><b>Кому:</b> активний користувач, який сам зможе керувати документом — за РОЛЯМИ бачить проєкт із
/// <c>Document.View</c> і рівнем ≥ <c>Write</c>. Інакше власник, що не може керувати власним документом, — 422
/// <c>documentOwnerInvalid</c> тим самим текстом, що й для відсутнього (без розкриття існування облікового запису).</item>
/// <item><b>Блокування:</b> рядок документа під <c>UPDLOCK</c> у транзакції — дві одночасні передачі не перезапишуть одна одну мовчки.</item>
/// </list>
/// </remarks>
public sealed class TransferDocumentOwnerHandler(
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки: власника документа змінено.</summary>
    public const string TransferredEvent = "DocumentOwnerTransferred";

    /// <summary>Передає власність; повертає нового власника.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="request">Тіло.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Документ невидимий.</exception>
    /// <exception cref="AccessDeniedException">Викликач не власник і не менеджер проєкту.</exception>
    /// <exception cref="BusinessRuleException">Новий власник недопустимий.</exception>
    public async Task<DocumentOwnerDto> HandleAsync(long documentId, TransferDocumentOwnerRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, access, profile, documentId, ct).ConfigureAwait(false);
        RequireOwnerOrManager(profile, document);

        var newOwnerId = request.NewOwnerUserId;
        if (newOwnerId <= 0 || newOwnerId == document.CreatedByUserId)
        {
            throw OwnerInvalid();
        }

        var users = await store.GetUsersAsync([newOwnerId], ct).ConfigureAwait(false);
        if (!users.TryGetValue(newOwnerId, out var newOwner) || !newOwner.IsActive)
        {
            throw OwnerInvalid();
        }

        // Новий власник мусить сам керувати документом (та сама умова, що в CanManage для власника).
        var candidate = await EditAssignmentAccess.TryBuildProfileAsync(access, newOwnerId, ct).ConfigureAwait(false);
        if (candidate is null
            || !candidate.SeesDocumentsOf(document.ProjectId)
            || !PermissionCheck.IsGrantedIn(candidate, ListDocumentsHandler.Permission, document.ProjectId)
            || candidate.LevelFor(ResourceKind.Project, document.ProjectId) < GrantLevel.Write)
        {
            throw OwnerInvalid();
        }

        var now = clock.UtcNow;
        var previousOwnerId = 0;

        await EditAssignmentAccess.InTransactionAsync(uow, async innerCt =>
        {
            await store.LimitLockWaitAsync(innerCt).ConfigureAwait(false);

            var tracked = await store.FindDocumentForOwnerChangeAsync(documentId, innerCt).ConfigureAwait(false)
                          ?? throw EditAssignmentAccess.NotFound(documentId);

            // Власника змінили між перевіркою прав і блокуванням — рішення приймалося про іншого власника.
            if (tracked.CreatedByUserId != document.CreatedByUserId)
            {
                throw EditAssignmentAccess.Stale(string.Empty);
            }

            previousOwnerId = tracked.TransferOwnership(newOwnerId, userId, now);

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, TransferredEvent, newOwnerId, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        previousOwnerUserId = previousOwnerId,
                        newOwnerUserId = newOwnerId,
                    }),
                    userId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new DocumentOwnerDto(documentId, newOwnerId, newOwner.DisplayName, previousOwnerId);
    }

    /// <summary>Вимагає права керування документом або 403 <c>documentOwnerTransferForbidden</c>.</summary>
    private static void RequireOwnerOrManager(AccessProfile profile, EditAssignmentDocumentRef document)
    {
        if (EditAssignmentAccess.CanManage(profile, document))
        {
            return;
        }

        throw new AccessDeniedException(
            Domain.Errors.ErrorCodes.AccessDenied, "Передати власність може власник документа або менеджер проєкту.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.documentOwnerTransferForbidden",
                ["documentId"] = document.Id.ToString(CultureInfo.InvariantCulture),
            });
    }

    private static BusinessRuleException OwnerInvalid()
        => EditAssignmentAccess.Invalid(
            "err.ECR-REQ-0422.documentOwnerInvalid", "Цього користувача не можна призначити власником документа.");
}
