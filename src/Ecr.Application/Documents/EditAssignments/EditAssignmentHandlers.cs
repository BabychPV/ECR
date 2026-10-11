// src/Ecr.Application/Documents/EditAssignments/EditAssignmentHandlers.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>
/// Передача місяця третій стороні (EA-C): перелік передач документа для власника.
/// </summary>
/// <remarks>
/// Передачі з аркушами, схованими від викликача, не повертаються (R9). Виконавець користується
/// <see cref="ListMyEditAssignmentsHandler"/>, а не цим переліком.
/// </remarks>
public sealed class ListEditAssignmentsHandler(
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IDocumentStore documents,
    IMetadataCache metadata,
    Common.ICurrentUser currentUser)
{
    /// <summary>Повертає передачі документа.</summary>
    /// <exception cref="NotFoundException">Документ невидимий.</exception>
    /// <exception cref="AccessDeniedException">Викликач не автор і не менеджер проєкту.</exception>
    public async Task<EditAssignmentListDto> HandleAsync(long documentId, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var scope = DocumentReadScope.For(profile, document.ProjectId, snapshot);

        var all = await assignments.ListByDocumentAsync(documentId, ct).ConfigureAwait(false);
        var visible = all
            .Where(a => a.SheetCode is null || scope.CanReadSheetCode(a.SheetCode))
            .OrderBy(a => a.PeriodKeyValue ?? 0).ThenBy(a => a.MonthNumber ?? 0).ThenBy(a => a.SliceKey, StringComparer.Ordinal)
            .ToList();

        var ids = new HashSet<int> { document.CreatedByUserId };
        foreach (var a in visible)
        {
            ids.Add(a.DelegatedByUserId);
            if (a.ValidatedByUserId is { } v)
            {
                ids.Add(v);
            }

            ids.UnionWith(a.Recipients.Select(r => r.UserId));
        }

        var users = await store.GetUsersAsync(ids, ct).ConfigureAwait(false);
        var items = visible.Select(a => EditAssignmentMapper.ToDto(a, users)).ToList();

        return new EditAssignmentListDto(
            users.TryGetValue(document.CreatedByUserId, out var owner) ? owner.DisplayName : string.Empty,
            CanManage: true,
            items);
    }
}

/// <summary>Мапінг сутностей у DTO.</summary>
internal static class EditAssignmentMapper
{
    public static EditAssignmentDto ToDto(EditAssignment a, IReadOnlyDictionary<int, EditAssignmentUserRef> users)
    {
        var recipients = a.Recipients
            .OrderBy(r => users.TryGetValue(r.UserId, out var u) ? u.DisplayName : string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new EditAssignmentRecipientDto(
                r.UserId,
                users.TryGetValue(r.UserId, out var u) ? u.DisplayName : string.Empty,
                r.Status.ToString(),
                r.FilledAt,
                r.ReturnedAt,
                r.ReturnReason,
                r.ReturnCount,
                users.TryGetValue(r.UserId, out var user) && user.IsActive))
            .ToList();

        return new EditAssignmentDto(
            a.Id,
            EditAssignmentAccess.ToDto(a.Slice),
            a.IsExclusive,
            EditAssignmentVersion.StatusOf(a),
            a.ValidatedAt,
            a.ValidatedByUserId is { } v && users.TryGetValue(v, out var by) ? by.DisplayName : null,
            a.DelegatedByUserId,
            users.TryGetValue(a.DelegatedByUserId, out var d) ? d.DisplayName : string.Empty,
            a.Note,
            a.AppliedSetId,
            a.AppliedSetName,
            recipients,
            a.ModifiedAt,
            EditAssignmentVersion.Of(a));
    }

    /// <summary>Текст листа: подробиці зрізу людською мовою (англійська — базова мова каталогу).</summary>
    public static string DescribeSlice(EditAssignment a)
    {
        var parts = new List<string>();
        if (a.MonthNumber is { } m)
        {
            parts.Add($"month {m.ToString(CultureInfo.InvariantCulture)}");
        }

        if (a.PeriodKeyValue is { } p)
        {
            parts.Add($"period {p.ToString(CultureInfo.InvariantCulture)}");
        }

        if (a.SheetCode is not null)
        {
            parts.Add($"sheet {a.SheetCode}");
        }

        if (a.TableCode is not null)
        {
            parts.Add($"table {a.TableCode}");
        }

        return parts.Count == 0 ? "whole document" : string.Join(", ", parts);
    }
}

/// <summary>
/// Створення/зміна передачі (upsert за зрізом) і набір виконавців.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Права:</b> автор документа або менеджер проєкту; «у межах власних прав» перевіряється
/// ПРИ ПЕРЕДАЧІ (§3.3 DESIGN) у сесії того, хто передає.</item>
/// <item><b>Одержувач МАЄ Read</b> (Q1): без видимості документа — 422 <c>recipientInvalid</c> тим самим
/// текстом, що й для неіснуючого чи вимкненого користувача (без розкриття існування).</item>
/// <item><b>Ідемпотентність:</b> повтор із тим самим складом і ознаками — 200 без змін; зміна наявної
/// передачі вимагає <c>baseVersion</c>.</item>
/// </list>
/// </remarks>
public sealed class UpsertEditAssignmentHandler(
    IEditAssignmentStore assignments,
    IEditorSetStore sets,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IDocumentStore documents,
    IMetadataCache metadata,
    IUnitOfWork uow,
    IAuditWriter audit,
    INotificationOutbox outbox,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки: надано/додано виконавця.</summary>
    public const string GrantedEvent = "EditAssignmentGranted";

    /// <summary>Тип події журналу безпеки: виконавця знято.</summary>
    public const string RevokedEvent = "EditAssignmentRevoked";

    /// <summary>Тип події журналу безпеки: змінено ознаки передачі.</summary>
    public const string ChangedEvent = "EditAssignmentChanged";

    /// <summary>Код події сповіщення: виконавцю передано зріз.</summary>
    public const string AssignedNotification = "edit.assigned";

    /// <summary>Виконує upsert; повертає передачу в її стані після збереження.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="request">Тіло.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="overwrite">
    /// Явне перезаписування складу (apply-set/copy): <c>baseVersion</c> наявної передачі не потрібна.
    /// </param>
    public async Task<EditAssignmentDto> HandleAsync(
        long documentId, UpsertEditAssignmentRequest request, CancellationToken ct, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var slice = EditAssignmentAccess.ToSlice(request.Slice);

        var note = request.Note?.Trim();
        if (note is { Length: > EditAssignment.MaxNoteLength })
        {
            throw EditAssignmentAccess.Invalid("editAssignmentNoteTooLong", "Інструкція довша за 500 символів.");
        }

        var recipientIds = (request.RecipientUserIds ?? []).Distinct().ToList();
        if (recipientIds.Count == 0 || recipientIds.Contains(userId) || recipientIds.Contains(document.CreatedByUserId))
        {
            throw RecipientInvalid();
        }

        if (recipientIds.Count > EditAssignment.MaxRecipients)
        {
            throw EditAssignmentAccess.Invalid(
                "editAssignmentTooManyRecipients", "У передачі забагато виконавців.");
        }

        // «Лише в межах власних прав»: з даних шаблону, у сесії того, хто передає.
        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var own = EditAssignmentAccess.SheetsBeyondOwnRights(profile, document.ProjectId, snapshot, slice);
        if (own.Exceeds)
        {
            throw EditAssignmentAccess.Invalid(
                "editAssignmentDelegationExceedsOwnRights",
                "Передавати можна лише комірки, які ви самі можете редагувати.",
                new Dictionary<string, object?> { ["sheetCodes"] = own.VisibleSheetCodes });
        }

        // Одержувач мусить мати Read (Q1) — інакше передача не дасть жодного ефекту, а ознака «є/нема»
        // не розкривається: однакова відповідь для відсутнього, вимкненого й без видимості.
        var users = await store.GetUsersAsync(recipientIds, ct).ConfigureAwait(false);
        foreach (var id in recipientIds)
        {
            if (!users.TryGetValue(id, out var user) || !user.IsActive)
            {
                throw RecipientInvalid();
            }

            var recipientProfile = await access.BuildProfileAsync(id, ct).ConfigureAwait(false);
            if (!recipientProfile.SeesDocumentsOf(document.ProjectId)
                || !PermissionCheck.IsGrantedIn(recipientProfile, "Document.View", document.ProjectId))
            {
                throw RecipientInvalid();
            }
        }

        // Набір — лише власний (чужий ≡ відсутній); назва фіксується знімком.
        string? appliedSetName = null;
        if (request.AppliedSetId is { } setId)
        {
            var set = await sets.FindAsync(userId, setId, ct).ConfigureAwait(false)
                ?? throw EditAssignmentAccess.Invalid("editorSetNotFound", "Набору виконавців не знайдено.");
            appliedSetName = set.Name;
        }

        var now = clock.UtcNow;
        EditAssignment? result = null;

        try
        {
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⛔ Аудит P2: документ могли видалити між перевіркою прав і вставкою — блокуємо його рядок.
            if (!await store.LockDocumentAsync(documentId, innerCt).ConfigureAwait(false))
            {
                throw EditAssignmentAccess.NotFound(documentId);
            }

            var existing = await store
                .FindBySliceKeyForUpdateAsync(documentId, slice.Key, innerCt).ConfigureAwait(false);

            if (existing is not null)
            {
                await store.LockRecipientsAsync(existing.Id, innerCt).ConfigureAwait(false);
            }

            var events = new List<SecurityEventRecord>();
            var newlyAssigned = new List<int>();

            if (existing is null)
            {
                if (await assignments.CountByDocumentAsync(documentId, innerCt).ConfigureAwait(false) >= EditAssignment.MaxPerDocument)
                {
                    throw EditAssignmentAccess.Invalid(
                        "editAssignmentTooManyAssignments", "У документі забагато передач.");
                }

                var assignment = new EditAssignment(
                    documentId, slice, request.IsExclusive, delegatedByUserId: userId, Guid.NewGuid(), userId, now,
                    expiresAt: null, note: note);
                foreach (var id in recipientIds)
                {
                    assignment.AddRecipient(id, userId, now);
                    newlyAssigned.Add(id);
                }

                assignments.Add(assignment);
                result = assignment;
            }
            else
            {
                var sameMembers = existing.Recipients.Select(r => r.UserId).OrderBy(i => i)
                    .SequenceEqual(recipientIds.OrderBy(i => i));
                var sameFlags = existing.IsExclusive == request.IsExclusive
                    && string.Equals(existing.Note ?? string.Empty, note ?? string.Empty, StringComparison.Ordinal);

                if (sameMembers && sameFlags)
                {
                    // Ідемпотентний повтор: нічого не змінюємо й не журналюємо.
                    result = existing;
                    return;
                }

                if (!overwrite)
                {
                    EditAssignmentAccess.RequireVersion(request.BaseVersion, existing);
                }

                var removed = existing.Recipients.Where(r => !recipientIds.Contains(r.UserId)).Select(r => r.UserId).ToList();
                foreach (var id in removed)
                {
                    existing.RemoveRecipient(id, userId, now);
                }

                foreach (var id in recipientIds)
                {
                    if (existing.AddRecipient(id, userId, now))
                    {
                        newlyAssigned.Add(id);
                    }
                }

                // Додавання виконавця саме скидає валідацію (AddRecipient); знімання «Заповнено»-виконавця її не ламає.
                existing.Reconfigure(request.IsExclusive, note, userId, now);
                existing.Redelegate(userId, now);

                foreach (var id in removed)
                {
                    events.Add(Event(RevokedEvent, now, id, userId, documentId, existing, "recipientRemoved"));
                }

                if (!sameFlags)
                {
                    events.Add(Event(ChangedEvent, now, null, userId, documentId, existing, "flags"));
                }

                result = existing;
            }

            if (request.AppliedSetId is { } appliedId)
            {
                result!.MarkAppliedSet(appliedId, appliedSetName);
            }

            foreach (var id in newlyAssigned)
            {
                events.Add(Event(GrantedEvent, now, id, userId, documentId, result!, "grant"));
            }

            // Лист — лише тим, у кого є адреса; recipients: null (розсилка всім) заборонено.
            foreach (var id in newlyAssigned)
            {
                if (users.TryGetValue(id, out var u) && !string.IsNullOrWhiteSpace(u.Email))
                {
                    await outbox.EnqueueAsync(
                        AssignedNotification,
                        $"ECR: {document.BusinessKey} — please fill in {EditAssignmentMapper.DescribeSlice(result!)}",
                        BuildAssignedBody(document.BusinessKey, result!, note),
                        u.Email, innerCt).ConfigureAwait(false);
                }
            }

            if (events.Count > 0)
            {
                await audit.WriteSecurityEventsAsync(events, innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Двоє створили той самий зріз одночасно (або дубль іншого регістру): друга сторона бачить конфлікт.
            throw EditAssignmentAccess.Stale(string.Empty);
        }

        var ids = new HashSet<int> { result!.DelegatedByUserId };
        ids.UnionWith(result.Recipients.Select(r => r.UserId));
        var names = await store.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return EditAssignmentMapper.ToDto(result, names);
    }

    private SecurityEventRecord Event(
        string type, DateTime now, int? targetUserId, int byUserId, long documentId, EditAssignment a, string via)
        => new(
            now, type, targetUserId, TargetRoleId: null,
            JsonSerializer.Serialize(new
            {
                documentId,
                assignmentId = a.Id,
                slice = a.SliceKey,
                isExclusive = a.IsExclusive,
                via,
            }),
            byUserId, currentUser.CorrelationId);

    private static bool IsUniqueViolation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            // 2601 — дубль унікального індексу, 2627 — порушення обмеження UNIQUE.
            if (e.GetType().Name == "SqlException" && e.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static BusinessRuleException RecipientInvalid()
        => EditAssignmentAccess.Invalid(
            "editAssignmentRecipientInvalid", "Одного з виконавців не можна призначити на цей документ.");

    private static string BuildAssignedBody(string documentKey, EditAssignment a, string? note)
    {
        var text = $"Document {WebUtility.HtmlEncode(documentKey)}: you are assigned to fill in "
            + $"{WebUtility.HtmlEncode(EditAssignmentMapper.DescribeSlice(a))}.";
        return string.IsNullOrWhiteSpace(note) ? text : text + " Note: " + WebUtility.HtmlEncode(note);
    }
}

/// <summary>Зняття передачі (разом із виконавцями).</summary>
public sealed class DeleteEditAssignmentHandler(
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Знімає передачу; невідому чи чужого документа — «не знайдено».</summary>
    public async Task HandleAsync(long documentId, long assignmentId, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var now = clock.UtcNow;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var assignment = await assignments.FindForUpdateAsync(documentId, assignmentId, innerCt).ConfigureAwait(false)
                ?? throw EditAssignmentAccess.NotFound(documentId);
            await store.LockRecipientsAsync(assignment.Id, innerCt).ConfigureAwait(false);

            var events = assignment.Recipients
                .Select(r => new SecurityEventRecord(
                    now, UpsertEditAssignmentHandler.RevokedEvent, r.UserId, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        assignmentId,
                        slice = assignment.SliceKey,
                        via = "assignmentDeleted",
                    }),
                    userId, currentUser.CorrelationId))
                .ToList();

            if (events.Count == 0)
            {
                events.Add(new SecurityEventRecord(
                    now, UpsertEditAssignmentHandler.RevokedEvent, null, null,
                    JsonSerializer.Serialize(new { documentId, assignmentId, slice = assignment.SliceKey, via = "assignmentDeleted" }),
                    userId, currentUser.CorrelationId));
            }

            assignments.Remove(assignment);
            await audit.WriteSecurityEventsAsync(events, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// «Заповнено» — лише сам виконавець; після цього він не пише, доки власник не поверне.
/// </summary>
/// <remarks>
/// ⛔ Чужа чи неіснуюча передача, не-виконавець, невидимий документ — одна відповідь «не знайдено»
/// (T6/T7). Повтор — 200 без змін і без події (ідемпотентно). Рядок передачі береться під
/// <c>UPDLOCK</c>: запис комірки (EA-2) звіряє стан виконавця під тим самим блокуванням.
/// </remarks>
public sealed class FillEditAssignmentHandler(
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    INotificationOutbox outbox,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string FilledEvent = "EditAssignmentFilled";

    /// <summary>Код події сповіщення: власникові — зріз заповнено.</summary>
    public const string FilledNotification = "edit.filled";

    /// <summary>Позначає «Заповнено» для викликача.</summary>
    public async Task<EditAssignmentDto> HandleAsync(long documentId, long assignmentId, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);

        if (profile.IsSimulation || document.ProjectStatus == ProjectStatus.Archived || document.IsArchiving)
        {
            throw new AccessDeniedException(
                Domain.Errors.ErrorCodes.AccessDenied, "Документ доступний лише для читання.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-ACCS-0403.editAssignmentReadOnly" });
        }

        var now = clock.UtcNow;
        EditAssignment? result = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var assignment = await assignments.FindForUpdateAsync(documentId, assignmentId, innerCt).ConfigureAwait(false);
            if (assignment is null || assignment.FindRecipient(userId) is null)
            {
                throw EditAssignmentAccess.NotFound(documentId);
            }

            await store.LockRecipientsAsync(assignment.Id, innerCt).ConfigureAwait(false);

            result = assignment;
            if (!assignment.FillRecipient(userId, now))
            {
                return;
            }

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, FilledEvent, userId, TargetRoleId: null,
                    JsonSerializer.Serialize(new { documentId, assignmentId, slice = assignment.SliceKey }),
                    userId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            // Власнику — лише з адресою; recipients не null.
            var owners = await store
                .GetUsersAsync([assignment.DelegatedByUserId], innerCt).ConfigureAwait(false);
            if (assignment.DelegatedByUserId != userId
                && owners.TryGetValue(assignment.DelegatedByUserId, out var owner)
                && !string.IsNullOrWhiteSpace(owner.Email))
            {
                await outbox.EnqueueAsync(
                    FilledNotification,
                    $"ECR: {document.BusinessKey} — {EditAssignmentMapper.DescribeSlice(assignment)} filled",
                    $"Document {WebUtility.HtmlEncode(document.BusinessKey)}: "
                        + $"{WebUtility.HtmlEncode(EditAssignmentMapper.DescribeSlice(assignment))} is filled and awaits your validation.",
                    owner.Email, innerCt).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var ids = new HashSet<int> { result!.DelegatedByUserId };
        ids.UnionWith(result.Recipients.Select(r => r.UserId));
        var names = await store.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return EditAssignmentMapper.ToDto(result, names);
    }
}

/// <summary>Валідація власником: усі виконавці «Заповнено» → зріз «Валідовано».</summary>
public sealed class ValidateEditAssignmentHandler(
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    INotificationOutbox outbox,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string ValidatedEvent = "EditAssignmentValidated";

    /// <summary>Код події сповіщення: виконавцям — валідовано.</summary>
    public const string ValidatedNotification = "edit.validated";

    /// <summary>Валідує передачу (повтор на валідованій — 200 без змін).</summary>
    public async Task<EditAssignmentDto> HandleAsync(
        long documentId, long assignmentId, ValidateEditAssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var now = clock.UtcNow;
        EditAssignment? result = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var assignment = await assignments.FindForUpdateAsync(documentId, assignmentId, innerCt).ConfigureAwait(false)
                ?? throw EditAssignmentAccess.NotFound(documentId);
            await store.LockRecipientsAsync(assignment.Id, innerCt).ConfigureAwait(false);
            result = assignment;

            if (assignment.ValidationStatus == EditAssignmentValidationStatus.Validated)
            {
                return;
            }

            EditAssignmentAccess.RequireVersion(request.BaseVersion, assignment);

            if (!assignment.AllFilled)
            {
                throw EditAssignmentAccess.Invalid(
                    "editAssignmentNotAllFilled", "Валідувати можна лише після того, як усі виконавці заповнили зріз.");
            }

            assignment.Validate(userId, now);

            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now, ValidatedEvent, TargetUserId: null, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        assignmentId,
                        slice = assignment.SliceKey,
                        recipients = assignment.Recipients.Select(r => r.UserId).OrderBy(i => i).ToList(),
                    }),
                    userId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            var users = await store.GetUsersAsync(assignment.Recipients.Select(r => r.UserId).ToList(), innerCt)
                .ConfigureAwait(false);
            foreach (var r in assignment.Recipients)
            {
                if (users.TryGetValue(r.UserId, out var u) && !string.IsNullOrWhiteSpace(u.Email))
                {
                    await outbox.EnqueueAsync(
                        ValidatedNotification,
                        $"ECR: {document.BusinessKey} — {EditAssignmentMapper.DescribeSlice(assignment)} validated",
                        $"Document {WebUtility.HtmlEncode(document.BusinessKey)}: "
                            + $"{WebUtility.HtmlEncode(EditAssignmentMapper.DescribeSlice(assignment))} is validated.",
                        u.Email, innerCt).ConfigureAwait(false);
                }
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var ids = new HashSet<int> { result!.DelegatedByUserId, userId };
        ids.UnionWith(result.Recipients.Select(r => r.UserId));
        var names = await store.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return EditAssignmentMapper.ToDto(result, names);
    }
}

/// <summary>
/// Повернення на доопрацювання з причиною. Допустимо з «Заповнено» і з «Валідовано» (Q2): виконавець
/// знову пише, валідація скидається.
/// </summary>
public sealed class ReturnEditAssignmentHandler(
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    INotificationOutbox outbox,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки.</summary>
    public const string ReturnedEvent = "EditAssignmentReturned";

    /// <summary>Код події сповіщення: виконавцю — повернуто.</summary>
    public const string ReturnedNotification = "edit.returned";

    /// <summary>Повертає виконавців (за замовчуванням усіх «Заповнено»).</summary>
    public async Task<EditAssignmentDto> HandleAsync(
        long documentId, long assignmentId, ReturnEditAssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw EditAssignmentAccess.Invalid("editAssignmentReasonRequired", "Вкажіть причину повернення.");
        }

        if (reason.Length > EditAssignment.MaxNoteLength)
        {
            throw EditAssignmentAccess.Invalid("editAssignmentReasonTooLong", "Причина довша за 500 символів.");
        }

        var now = clock.UtcNow;
        EditAssignment? result = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var assignment = await assignments.FindForUpdateAsync(documentId, assignmentId, innerCt).ConfigureAwait(false)
                ?? throw EditAssignmentAccess.NotFound(documentId);
            await store.LockRecipientsAsync(assignment.Id, innerCt).ConfigureAwait(false);
            result = assignment;

            EditAssignmentAccess.RequireVersion(request.BaseVersion, assignment);

            var members = assignment.Recipients.ToDictionary(r => r.UserId);
            List<EditAssignmentRecipient> targets;
            if (request.UserIds is { Count: > 0 })
            {
                var requested = request.UserIds.Distinct().ToList();
                if (requested.Any(id => !members.ContainsKey(id)))
                {
                    throw EditAssignmentAccess.Invalid(
                        "editAssignmentRecipientInvalid", "Одного з виконавців не можна призначити на цей документ.");
                }

                targets = [.. requested.Select(id => members[id])];
            }
            else
            {
                targets = [.. assignment.Recipients.Where(r => r.Status == EditAssignmentRecipientStatus.Filled)];
            }

            var returned = new List<EditAssignmentRecipient>();
            foreach (var target in targets)
            {
                if (target.Status == EditAssignmentRecipientStatus.Filled)
                {
                    assignment.ReturnRecipient(target.UserId, userId, reason, now);
                    returned.Add(target);
                }
            }

            if (returned.Count == 0)
            {
                // Усі вказані вже повернуті — ідемпотентний повтор; інакше (ще не заповнено) — конфлікт стану.
                if (targets.Count > 0 && targets.All(t => t.Status == EditAssignmentRecipientStatus.Returned))
                {
                    return;
                }

                throw EditAssignmentAccess.WrongState("NotFilled");
            }

            var events = returned
                .Select(r => new SecurityEventRecord(
                    now, ReturnedEvent, r.UserId, TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        documentId,
                        assignmentId,
                        slice = assignment.SliceKey,
                        reason,
                    }),
                    userId, currentUser.CorrelationId))
                .ToList();
            await audit.WriteSecurityEventsAsync(events, innerCt).ConfigureAwait(false);

            var users = await store.GetUsersAsync(returned.Select(r => r.UserId).ToList(), innerCt)
                .ConfigureAwait(false);
            foreach (var r in returned)
            {
                if (users.TryGetValue(r.UserId, out var u) && !string.IsNullOrWhiteSpace(u.Email))
                {
                    await outbox.EnqueueAsync(
                        ReturnedNotification,
                        $"ECR: {document.BusinessKey} — {EditAssignmentMapper.DescribeSlice(assignment)} returned",
                        $"Document {WebUtility.HtmlEncode(document.BusinessKey)}: "
                            + $"{WebUtility.HtmlEncode(EditAssignmentMapper.DescribeSlice(assignment))} is returned for rework: "
                            + WebUtility.HtmlEncode(reason),
                        u.Email, innerCt).ConfigureAwait(false);
                }
            }

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var ids = new HashSet<int> { result!.DelegatedByUserId };
        ids.UnionWith(result.Recipients.Select(r => r.UserId));
        var names = await store.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return EditAssignmentMapper.ToDto(result, names);
    }
}

/// <summary>
/// Пошук кандидатів у виконавці: активні користувачі за частиною імені; не менше двох символів;
/// без email, SID і ролей (T8).
/// </summary>
public sealed class ListEditAssignmentCandidatesHandler(
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Межа відповіді.</summary>
    public const int MaxResults = 20;

    /// <summary>Мінімальна довжина запиту.</summary>
    public const int MinQueryLength = 2;

    /// <summary>Повертає кандидатів.</summary>
    public async Task<IReadOnlyList<EditAssignmentCandidateDto>> HandleAsync(
        long documentId, string? query, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);

        var q = query?.Trim();
        if (string.IsNullOrEmpty(q) || q.Length < MinQueryLength)
        {
            throw EditAssignmentAccess.Invalid(
                "editAssignmentQueryTooShort", "Запит має містити щонайменше два символи.");
        }

        var found = await store.SearchCandidatesAsync(q, MaxResults + 1, ct).ConfigureAwait(false);
        return [.. found
            .Where(u => u.Id != userId && u.Id != document.CreatedByUserId)
            .Take(MaxResults)
            .Select(u => new EditAssignmentCandidateDto(u.Id, u.DisplayName))];
    }
}

/// <summary>
/// «Мої задачі» за передачами: «Assigned to me» і «Awaiting my validation». Лише рядки самого
/// викликача; імен інших виконавців немає.
/// </summary>
public sealed class ListMyEditAssignmentsHandler(
    IEditAssignmentManagementStore store,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Повертає мої передачі; <paramref name="role"/> — <c>assignee</c> | <c>owner</c> | порожньо (обидва).</summary>
    public async Task<MyEditAssignmentsDto> HandleAsync(string? role, long? documentId, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);

        var wantAssignee = string.IsNullOrEmpty(role) || string.Equals(role, "assignee", StringComparison.OrdinalIgnoreCase);
        var wantOwner = string.IsNullOrEmpty(role) || string.Equals(role, "owner", StringComparison.OrdinalIgnoreCase);
        if (!wantAssignee && !wantOwner)
        {
            throw EditAssignmentAccess.Invalid("editAssignmentRoleInvalid", "Невідома роль фільтра.");
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var rows = await store.ListMineAsync(userId, documentId, ct).ConfigureAwait(false);
        var visible = rows.Where(r => profile.SeesDocumentsOf(r.ProjectId)).ToList();

        var assigned = wantAssignee
            ? visible
                .Where(r => r.MyStatus is EditAssignmentRecipientStatus.Assigned or EditAssignmentRecipientStatus.Returned)
                .Select(r => ToDto(r, r.MyStatus!.Value.ToString()))
                .ToList()
            : [];

        var awaiting = wantOwner
            ? visible
                .Where(r => r.IsOwnerOf
                    && r.ValidationStatus == EditAssignmentValidationStatus.NotValidated
                    && r.RecipientCount > 0 && r.FilledCount == r.RecipientCount)
                .Select(r => ToDto(r, "Filled"))
                .ToList()
            : [];

        return new MyEditAssignmentsDto(assigned, awaiting);
    }

    private static MyEditAssignmentDto ToDto(MyEditAssignmentRow r, string status)
        => new(
            r.AssignmentId, r.DocumentId, r.DocumentKey,
            new EditAssignmentSliceDto(r.SheetCode, r.TableCode, r.MonthNumber, r.PeriodKey),
            status,
            r.MyReturnReason,
            r.Note,
            r.DelegatedByDisplayName,
            r.RecipientCount,
            r.FilledCount,
            r.MyStatusChangedAt,
            EditAssignmentVersion.OfBytes(r.Version));
}
