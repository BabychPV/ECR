// src/Ecr.Application/Documents/EditAssignments/EditorSetHandlers.cs
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>Член набору виконавців.</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="DisplayName">Ім'я.</param>
/// <param name="IsActive">Обліковий запис не вимкнений.</param>
public sealed record EditorSetMemberDto(int UserId, string DisplayName, bool IsActive);

/// <summary>Набір виконавців власника.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Name">Назва.</param>
/// <param name="Members">Члени.</param>
/// <param name="Version">Версія для <c>baseVersion</c>.</param>
public sealed record EditorSetDto(int Id, string Name, IReadOnlyList<EditorSetMemberDto> Members, string Version);

/// <summary>Тіло створення/зміни набору.</summary>
/// <param name="Name">Назва 1..100.</param>
/// <param name="MemberUserIds">Члени (≤ 500).</param>
/// <param name="BaseVersion">Обов'язкова для зміни (<c>PUT</c>).</param>
public sealed record SaveEditorSetRequest(string Name, IReadOnlyList<int> MemberUserIds, string? BaseVersion = null);

/// <summary>Тіло «застосувати набір до зрізів».</summary>
/// <param name="SetId">Набір власника.</param>
/// <param name="Slices">Зрізи (1..12).</param>
/// <param name="IsExclusive">Ексклюзивність.</param>
/// <param name="Note">Інструкція.</param>
public sealed record ApplyEditorSetRequest(
    int SetId, IReadOnlyList<EditAssignmentSliceDto> Slices, bool IsExclusive = true, string? Note = null);

/// <summary>Тіло «скопіювати передачу на інші зрізи» (наприклад, на наступний місяць).</summary>
/// <param name="FromSlice">Зріз-джерело (наявна передача).</param>
/// <param name="ToSlices">Зрізи-цілі (1..12).</param>
public sealed record CopyEditAssignmentRequest(
    EditAssignmentSliceDto FromSlice, IReadOnlyList<EditAssignmentSliceDto> ToSlices);

/// <summary>Відповідь «застосувати набір»/«скопіювати»: створені чи оновлені передачі.</summary>
/// <param name="Assignments">Передачі за порядком зрізів у запиті.</param>
public sealed record EditAssignmentBatchDto(IReadOnlyList<EditAssignmentDto> Assignments);

/// <summary>
/// Набори виконавців власника: перелік, створення, зміна, видалення. Лише власні набори; чужий набір ≡
/// відсутній (T7).
/// </summary>
public sealed class EditorSetsHandler(
    IEditorSetStore sets,
    IEditAssignmentManagementStore users,
    IUnitOfWork uow,
    IAuditWriter audit,
    Common.ICurrentUser currentUser,
    Domain.Abstractions.IClock clock)
{
    /// <summary>Тип події журналу безпеки: створено/змінено набір.</summary>
    public const string ChangedEvent = "EditorSetChanged";

    /// <summary>Тип події журналу безпеки: видалено набір.</summary>
    public const string DeletedEvent = "EditorSetDeleted";

    /// <summary>Набори власника.</summary>
    public async Task<IReadOnlyList<EditorSetDto>> ListAsync(CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var all = await sets.ListByOwnerAsync(userId, ct).ConfigureAwait(false);
        var ids = all.SelectMany(s => s.Members.Select(m => m.UserId)).Distinct().ToList();
        var names = await users.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return [.. all.Select(s => ToDto(s, names))];
    }

    /// <summary>Створює набір.</summary>
    public async Task<EditorSetDto> CreateAsync(SaveEditorSetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var members = await ValidateAsync(request, userId, ct).ConfigureAwait(false);
        var name = request.Name.Trim();
        var now = clock.UtcNow;
        EditorSet? created = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            if (await sets.CountByOwnerAsync(userId, innerCt).ConfigureAwait(false) >= EditorSet.MaxPerOwner)
            {
                throw EditAssignmentAccess.Invalid("editorSetTooMany", "Забагато наборів виконавців.");
            }

            if (await sets.NameExistsAsync(userId, name, null, innerCt).ConfigureAwait(false))
            {
                throw EditAssignmentAccess.Invalid("editorSetNameTaken", "Набір із такою назвою вже є.");
            }

            var set = new EditorSet(userId, name, now);
            sets.Add(set);
            // Id набору потрібен членам до збереження: спершу зберігаємо порожній набір.
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            set.ReplaceMembers(members, now);
            await audit.WriteSecurityEventAsync(Event(ChangedEvent, now, userId, set.Id, name, members, "created"), innerCt)
                .ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            created = set;
        }, ct).ConfigureAwait(false);

        var names = await users.GetUsersAsync(members, ct).ConfigureAwait(false);
        return ToDto(created!, names);
    }

    /// <summary>Змінює назву й склад набору (потрібна <c>baseVersion</c>).</summary>
    public async Task<EditorSetDto> UpdateAsync(int setId, SaveEditorSetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var members = await ValidateAsync(request, userId, ct).ConfigureAwait(false);
        var name = request.Name.Trim();
        var now = clock.UtcNow;
        EditorSet? updated = null;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var set = await sets.FindAsync(userId, setId, innerCt).ConfigureAwait(false) ?? throw SetNotFound();

            var actual = EditAssignmentVersion.OfBytes(set.RowVersion);
            if (string.IsNullOrWhiteSpace(request.BaseVersion))
            {
                throw EditAssignmentAccess.Invalid("editorSetBaseVersion", "Запит має нести baseVersion набору.");
            }

            if (!string.Equals(actual, request.BaseVersion.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new ConcurrencyConflictException(
                    Domain.Errors.ErrorCodes.DocumentSubmitted, "Набір змінили після того, як його відкрили.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-DOC-0409.editorSetStale",
                        ["version"] = actual,
                    });
            }

            if (await sets.NameExistsAsync(userId, name, setId, innerCt).ConfigureAwait(false))
            {
                throw EditAssignmentAccess.Invalid("editorSetNameTaken", "Набір із такою назвою вже є.");
            }

            set.Rename(name, now);
            set.ReplaceMembers(members, now);
            await audit.WriteSecurityEventAsync(Event(ChangedEvent, now, userId, set.Id, name, members, "updated"), innerCt)
                .ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            updated = set;
        }, ct).ConfigureAwait(false);

        var names = await users.GetUsersAsync(members, ct).ConfigureAwait(false);
        return ToDto(updated!, names);
    }

    /// <summary>Видаляє набір; передачі, застосовані з нього, лишаються (знімок), посилання обнуляється.</summary>
    public async Task DeleteAsync(int setId, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var set = await sets.FindAsync(userId, setId, innerCt).ConfigureAwait(false) ?? throw SetNotFound();
            var memberIds = set.Members.Select(m => m.UserId).ToList();
            await sets.RemoveAsync(set, innerCt).ConfigureAwait(false);
            await audit.WriteSecurityEventAsync(Event(DeletedEvent, now, userId, setId, set.Name, memberIds, "deleted"), innerCt)
                .ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<List<int>> ValidateAsync(SaveEditorSetRequest request, int ownerId, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > EditorSet.MaxNameLength || name.Any(char.IsControl))
        {
            throw EditAssignmentAccess.Invalid("editorSetNameInvalid", "Назва набору — 1..100 символів без керувальних.");
        }

        var ids = (request.MemberUserIds ?? []).Distinct().ToList();
        if (ids.Count > EditorSet.MaxMembers)
        {
            throw EditAssignmentAccess.Invalid("editorSetTooManyMembers", "У наборі забагато виконавців.");
        }

        if (ids.Count > 0)
        {
            var found = await users.GetUsersAsync(ids, ct).ConfigureAwait(false);
            if (ids.Any(id => id == ownerId || !found.TryGetValue(id, out var u) || !u.IsActive))
            {
                throw EditAssignmentAccess.Invalid("editorSetMemberInvalid", "Одного з виконавців не можна додати до набору.");
            }
        }

        return ids;
    }

    private SecurityEventRecord Event(
        string type, DateTime now, int ownerId, int setId, string name, IReadOnlyCollection<int> members, string via)
        => new(
            now, type, TargetUserId: null, TargetRoleId: null,
            JsonSerializer.Serialize(new { setId, name, members = members.OrderBy(i => i).ToList(), via }),
            ownerId, currentUser.CorrelationId);

    private static NotFoundException SetNotFound()
        => new(
            Domain.Errors.ErrorCodes.DocumentNotFound, "Набір виконавців не знайдено.",
            new Dictionary<string, object?> { ["messageKey"] = EditAssignmentAccess.DocumentKey });

    private static EditorSetDto ToDto(EditorSet set, IReadOnlyDictionary<int, EditAssignmentUserRef> names)
        => new(
            set.Id,
            set.Name,
            [.. set.Members
                .Select(m => new EditorSetMemberDto(
                    m.UserId,
                    names.TryGetValue(m.UserId, out var u) ? u.DisplayName : string.Empty,
                    names.TryGetValue(m.UserId, out var u2) && u2.IsActive))
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)],
            EditAssignmentVersion.OfBytes(set.RowVersion));
}

/// <summary>
/// «Застосувати набір до зрізів» і «Скопіювати передачу на наступний місяць»: послідовні upsert-и
/// (кожен — власна транзакція) з явним перезаписуванням складу. Ліміт — 12 зрізів за виклик.
/// </summary>
/// <remarks>
/// ⚠ Не атомарно по зрізах: відмова на k-му зрізі лишає попередні застосованими (кожен зріз
/// ідемпотентний — повтор запиту доводить решту).
/// </remarks>
public sealed class BatchEditAssignmentsHandler(
    UpsertEditAssignmentHandler upsert,
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IEditorSetStore sets,
    IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Межа зрізів за виклик.</summary>
    public const int MaxSlices = 12;

    /// <summary>Застосовує набір до зрізів.</summary>
    public async Task<EditAssignmentBatchDto> ApplySetAsync(
        long documentId, ApplyEditorSetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        await RequireManagerAsync(userId, documentId, ct).ConfigureAwait(false);
        RequireSlices(request.Slices);

        var set = await sets.FindAsync(userId, request.SetId, ct).ConfigureAwait(false)
            ?? throw EditAssignmentAccess.Invalid("editorSetNotFound", "Набору виконавців не знайдено.");
        var members = set.Members.Select(m => m.UserId).ToList();

        var result = new List<EditAssignmentDto>();
        foreach (var slice in request.Slices)
        {
            result.Add(await upsert.HandleAsync(
                documentId,
                new UpsertEditAssignmentRequest(slice, members, request.IsExclusive, request.Note, set.Id),
                ct, overwrite: true).ConfigureAwait(false));
        }

        return new EditAssignmentBatchDto(result);
    }

    /// <summary>Копіює склад і ознаки наявної передачі на інші зрізи.</summary>
    public async Task<EditAssignmentBatchDto> CopyAsync(
        long documentId, CopyEditAssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        await RequireManagerAsync(userId, documentId, ct).ConfigureAwait(false);
        RequireSlices(request.ToSlices);

        var from = EditAssignmentAccess.ToSlice(request.FromSlice);
        var source = (await assignments.ListByDocumentAsync(documentId, ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.SliceKey, from.Key, StringComparison.Ordinal))
            ?? throw EditAssignmentAccess.NotFound(documentId);
        var members = source.Recipients.Select(r => r.UserId).ToList();

        var result = new List<EditAssignmentDto>();
        foreach (var slice in request.ToSlices)
        {
            result.Add(await upsert.HandleAsync(
                documentId,
                new UpsertEditAssignmentRequest(slice, members, source.IsExclusive, source.Note, source.AppliedSetId),
                ct, overwrite: true).ConfigureAwait(false));
        }

        return new EditAssignmentBatchDto(result);
    }

    private async Task RequireManagerAsync(int userId, long documentId, CancellationToken ct)
    {
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);
    }

    private static void RequireSlices(IReadOnlyList<EditAssignmentSliceDto>? slices)
    {
        if (slices is null || slices.Count == 0 || slices.Count > MaxSlices)
        {
            throw EditAssignmentAccess.Invalid(
                "editAssignmentTooManySlices", "За один виклик можна вказати від 1 до 12 зрізів.");
        }
    }
}
