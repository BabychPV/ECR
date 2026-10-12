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
    IAccessDecisionService access,
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
        var userId = await RequireWriterAsync(ct).ConfigureAwait(false);
        var all = await sets.ListByOwnerAsync(userId, ct).ConfigureAwait(false);
        var ids = all.SelectMany(s => s.Members.Select(m => m.UserId)).Distinct().ToList();
        var names = await users.GetUsersAsync(ids, ct).ConfigureAwait(false);
        return [.. all.Select(s => ToDto(s, names))];
    }

    /// <summary>Створює набір.</summary>
    public async Task<EditorSetDto> CreateAsync(SaveEditorSetRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = await RequireWriterAsync(ct).ConfigureAwait(false);
        var members = await ValidateAsync(request, userId, ct).ConfigureAwait(false);
        var name = request.Name.Trim();
        var now = clock.UtcNow;
        EditorSet? created = null;

        await RunUniqueAsync(async innerCt =>
        {
            if (await sets.CountByOwnerAsync(userId, innerCt).ConfigureAwait(false) >= EditorSet.MaxPerOwner)
            {
                throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetTooMany", "Забагато наборів виконавців.");
            }

            if (await sets.NameExistsAsync(userId, name, null, innerCt).ConfigureAwait(false))
            {
                throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetNameTaken", "Набір із такою назвою вже є.");
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

        var userId = await RequireWriterAsync(ct).ConfigureAwait(false);
        var members = await ValidateAsync(request, userId, ct).ConfigureAwait(false);
        var name = request.Name.Trim();
        var now = clock.UtcNow;
        EditorSet? updated = null;

        await RunUniqueAsync(async innerCt =>
        {
            var set = await sets.FindAsync(userId, setId, innerCt).ConfigureAwait(false) ?? throw SetNotFound();

            var actual = EditAssignmentVersion.OfBytes(set.RowVersion);
            if (string.IsNullOrWhiteSpace(request.BaseVersion))
            {
                throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetBaseVersion", "Запит має нести baseVersion набору.");
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
                throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetNameTaken", "Набір із такою назвою вже є.");
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
        var userId = await RequireWriterAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        await RunUniqueAsync(async innerCt =>
        {
            var set = await sets.FindAsync(userId, setId, innerCt).ConfigureAwait(false) ?? throw SetNotFound();
            var memberIds = set.Members.Select(m => m.UserId).ToList();
            await sets.RemoveAsync(set, innerCt).ConfigureAwait(false);
            await audit.WriteSecurityEventAsync(Event(DeletedEvent, now, userId, setId, set.Name, memberIds, "deleted"), innerCt)
                .ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Транзакція зміни набору; дубль унікального імені при гонці «перевірив — вставив» (UQ_EditorSet_Owner)
    /// дає 422 <c>editorSetNameTaken</c>, а не 500 (аудит P3).
    /// </summary>
    private async Task RunUniqueAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteInTransactionAsync(operation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (EditAssignmentAccess.IsUniqueViolation(ex))
        {
            throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetNameTaken", "Набір із такою назвою вже є.");
        }
    }
    /// <summary>
    /// Набори виконавців — лише для користувачів із правом запису хоча б у одному проєкті (аудит S2): інакше
    /// перевірка складу (`ValidateAsync`) і ім'я в відповіді давали б оракул «є/активний» за довільним Id.
    /// </summary>
    private async Task<int> RequireWriterAsync(CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireWriteInAnyProject(profile);
        return userId;
    }
    private async Task<List<int>> ValidateAsync(SaveEditorSetRequest request, int ownerId, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > EditorSet.MaxNameLength || name.Any(char.IsControl))
        {
            throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetNameInvalid", "Назва набору — 1..100 символів без керувальних.");
        }

        var ids = (request.MemberUserIds ?? []).Distinct().ToList();
        if (ids.Count > EditorSet.MaxMembers)
        {
            throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetTooManyMembers", "У наборі забагато виконавців.");
        }

        if (ids.Count > 0)
        {
            // Єдина відповідь для «немає / вимкнений / сам власник / без жодного Read» (аудит P2-3): інакше набір
            // виходив би оракулом існування облікових записів з іменем у відповіді.
            var found = await users.GetUsersAsync(ids, ct).ConfigureAwait(false);
            foreach (var id in ids)
            {
                var valid = id != ownerId
                    && found.TryGetValue(id, out var u)
                    && u.IsActive
                    && EditAssignmentAccess.HasReadInAnyProject(
                        await access.BuildProfileAsync(id, ct).ConfigureAwait(false));

                if (!valid)
                {
                    throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetMemberInvalid", "Одного з виконавців не можна додати до набору.");
                }
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
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-DOC-0404.editorSet" });

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
/// «Застосувати набір до зрізів» і «Скопіювати передачу на наступний місяць»: ВСІ зрізи перевіряються
/// до першого запису (відмова на k-му не лишає 1..k-1), далі — послідовні upsert-и (кожен — власна
/// транзакція) з явним перезаписуванням складу. Ліміт — 12 зрізів за виклик.
/// </summary>
/// <remarks>
/// ⚠ Перезаписування явне й не вимагає <c>baseVersion</c> наявних передач: дія «застосувати набір/копіювати»
/// за задумом замінює склад зрізу; відповідь повертає КОЖНУ застосовану передачу з новою версією, а в
/// журнал безпеки йдуть події зняття/надання по виконавцях. Запис по зрізах не атомарний лише проти збоїв
/// бази й паралельних змін (кожен зріз ідемпотентний — повтор доводить решту).
/// </remarks>
public sealed class BatchEditAssignmentsHandler(
    UpsertEditAssignmentHandler upsert,
    IEditAssignmentStore assignments,
    IEditAssignmentManagementStore store,
    IEditorSetStore sets,
    IDocumentStore documents,
    IMetadataCache metadata,
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
            ?? throw EditAssignmentAccess.Invalid("err.ECR-REQ-0422.editorSetNotFound", "Набору виконавців не знайдено.");
        var members = set.Members.Select(m => m.UserId).ToList();
        var snapshot = new AppliedSetSnapshot(set.Id, set.Name);

        // Спершу ВСІ перевірки (права, межі власних прав, одержувачі), далі запис.
        var prepared = new List<PreparedUpsert>();
        foreach (var slice in request.Slices)
        {
            prepared.Add(await upsert.PrepareAsync(
                documentId,
                new UpsertEditAssignmentRequest(slice, members, request.IsExclusive, request.Note, set.Id),
                overwrite: true, snapshot, ct, keepExpiresAt: true).ConfigureAwait(false));
        }

        await RequireRoomAsync(documentId, prepared, ct).ConfigureAwait(false);
        return await CommitAllAsync(prepared, ct).ConfigureAwait(false);
    }

    /// <summary>Копіює склад і ознаки наявної передачі на інші зрізи.</summary>
    public async Task<EditAssignmentBatchDto> CopyAsync(
        long documentId, CopyEditAssignmentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var (profile, document) = await RequireManagerAsync(userId, documentId, ct).ConfigureAwait(false);
        RequireSlices(request.ToSlices);

        var from = EditAssignmentAccess.ToSlice(request.FromSlice);
        var source = (await assignments.ListByDocumentAsync(documentId, ct).ConfigureAwait(false))
            .FirstOrDefault(a => string.Equals(a.SliceKey, from.Key, StringComparison.Ordinal))
            ?? throw EditAssignmentAccess.AssignmentNotFound(documentId);

        // ⛔ Аудит P1-S1: зріз-джерело з аркушем, схованим від викликача, — та сама відповідь, що й «немає».
        var scope = await EditAssignmentAccess
            .ReadScopeAsync(documents, metadata, profile, document.ProjectId, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireSheetVisible(scope, source, documentId);

        var members = source.Recipients.Select(r => r.UserId).ToList();

        // Набір міг бути застосований ІНШИМ менеджером: його не шукаємо за власником, переносимо знімок.
        var snapshot = source.AppliedSetId is { } setId
            ? new AppliedSetSnapshot(setId, source.AppliedSetName ?? string.Empty)
            : null;

        var prepared = new List<PreparedUpsert>();
        foreach (var slice in request.ToSlices)
        {
            prepared.Add(await upsert.PrepareAsync(
                documentId,
                new UpsertEditAssignmentRequest(slice, members, source.IsExclusive, Note: null, source.AppliedSetId),
                overwrite: true, snapshot, ct, keepExpiresAt: true, keepNote: true).ConfigureAwait(false));
        }

        await RequireRoomAsync(documentId, prepared, ct).ConfigureAwait(false);
        return await CommitAllAsync(prepared, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ліміт передач документа перевіряється для ВСІЄЇ пачки до першого запису (аудит P3): відмова на k-му зрізі
    /// після запису 1..k-1 була б частковим застосуванням.
    /// </summary>
    private async Task RequireRoomAsync(long documentId, List<PreparedUpsert> prepared, CancellationToken ct)
    {
        var existing = (await assignments.ListByDocumentAsync(documentId, ct).ConfigureAwait(false))
            .Select(a => a.SliceKey).ToHashSet(StringComparer.Ordinal);
        var added = prepared.Select(p => p.Slice.Key).Distinct(StringComparer.Ordinal).Count(k => !existing.Contains(k));
        if (existing.Count + added > EditAssignment.MaxPerDocument)
        {
            throw EditAssignmentAccess.Invalid(
                "err.ECR-REQ-0422.editAssignmentTooManyAssignments", "У документі забагато передач.");
        }
    }
    private async Task<EditAssignmentBatchDto> CommitAllAsync(List<PreparedUpsert> prepared, CancellationToken ct)
    {
        var result = new List<EditAssignmentDto>(prepared.Count);
        foreach (var item in prepared)
        {
            result.Add(await upsert.CommitAsync(item, ct).ConfigureAwait(false));
        }

        return new EditAssignmentBatchDto(result);
    }

    private async Task<(AccessProfile Profile, EditAssignmentDocumentRef Document)> RequireManagerAsync(
        int userId, long documentId, CancellationToken ct)
    {
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var document = await EditAssignmentAccess
            .RequireVisibleDocumentAsync(store, profile, documentId, ct).ConfigureAwait(false);
        EditAssignmentAccess.RequireManager(profile, document);
        return (profile, document);
    }

    private static void RequireSlices(IReadOnlyList<EditAssignmentSliceDto>? slices)
    {
        if (slices is null || slices.Count == 0 || slices.Count > MaxSlices)
        {
            throw EditAssignmentAccess.Invalid(
                "err.ECR-REQ-0422.editAssignmentTooManySlices", "За один виклик можна вказати від 1 до 12 зрізів.");
        }
    }
}