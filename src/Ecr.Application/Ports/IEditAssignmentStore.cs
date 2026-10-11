// src/Ecr.Application/Ports/IEditAssignmentStore.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Передача виконавця користувачу в «Мої задачі» (<c>GET /me/edit-assignments</c>): лише ЙОГО рядок,
/// без імен інших виконавців (R9).
/// </summary>
/// <param name="AssignmentId">Передача.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="Slice">Зріз передачі.</param>
/// <param name="Status">Стан користувача як виконавця.</param>
/// <param name="ReturnReason">Причина повернення (лише для <c>Returned</c>).</param>
/// <param name="Note">Інструкція власника.</param>
/// <param name="DelegatedByUserId">Хто передав.</param>
/// <param name="ExpiresAt">Термін дії (UTC).</param>
/// <param name="StatusChangedAt">Коли стан змінювався востаннє (UTC).</param>
public sealed record EditAssignmentTaskRow(
    long AssignmentId,
    long DocumentId,
    EditAssignmentSlice Slice,
    EditAssignmentRecipientStatus Status,
    string? ReturnReason,
    string? Note,
    int DelegatedByUserId,
    DateTime? ExpiresAt,
    DateTime StatusChangedAt);

/// <summary>Факти передач документа для повторної перевірки запису під блокуванням (EA-B).</summary>
/// <param name="OwnerUserId">Автор документа.</param>
/// <param name="Facts">Передачі документа з погляду користувача.</param>
public sealed record EditAssignmentGateFacts(
    int OwnerUserId, IReadOnlyList<Ecr.Application.Security.EditAssignmentFact> Facts);

/// <summary>
/// Сховище передач зрізу (EA-1, <c>doc.EditAssignment*</c>). Усі методи — всередині
/// <see cref="IUnitOfWork.ExecuteInTransactionAsync"/> там, де вони пишуть; збереження — через
/// <see cref="IUnitOfWork.SaveChangesAsync"/>.
/// </summary>
/// <remarks>
/// ⛔ Вибірка конкретної передачі — завжди за ПАРОЮ <c>(DocumentId, Id)</c> (T7, IDOR): чужа передача
/// чи передача іншого документа дає <c>null</c>, а не 403 (одна відповідь «не знайдено»).
/// </remarks>
public interface IEditAssignmentStore
{
    /// <summary>Усі передачі документа з виконавцями (відстежувані).</summary>
    public Task<IReadOnlyList<EditAssignment>> ListByDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>Передача за парою <c>(documentId, assignmentId)</c> з виконавцями (відстежувана); немає — <c>null</c>.</summary>
    public Task<EditAssignment?> FindAsync(long documentId, long assignmentId, CancellationToken ct);

    /// <summary>
    /// Те саме, але рядок передачі береться під <c>UPDLOCK, ROWLOCK</c>: для переходів станів і повторної
    /// перевірки під sheet-gate (EA-2). Викликається ВСЕРЕДИНІ транзакції.
    /// </summary>
    public Task<EditAssignment?> FindForUpdateAsync(long documentId, long assignmentId, CancellationToken ct);

    /// <summary>Передача за канонічним ключем зрізу (upsert ідемпотентний); немає — <c>null</c>.</summary>
    public Task<EditAssignment?> FindBySliceKeyAsync(long documentId, string sliceKey, CancellationToken ct);

    /// <summary>Скільки передач у документа (ліміт <see cref="EditAssignment.MaxPerDocument"/>).</summary>
    public Task<int> CountByDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Факти передач документа для САМОГО користувача під <c>UPDLOCK, ROWLOCK</c> (EA-B): повторна
    /// перевірка передачі в транзакції запису, під блокуванням аркуша. Переходи станів чекають
    /// завершення цієї транзакції; відкликання/«Заповнено», зафіксовані раніше, видно тут.
    /// Викликається ВСЕРЕДИНІ транзакції.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="userId">Хто пише.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Автор документа й факти; порожній список — передач немає.</returns>
    public Task<EditAssignmentGateFacts> ReadFactsForGateAsync(long documentId, int userId, CancellationToken ct);

    /// <summary>Додає нову передачу в контекст (запис — <see cref="IUnitOfWork.SaveChangesAsync"/>).</summary>
    public void Add(EditAssignment assignment);

    /// <summary>Видаляє передачу разом із виконавцями (зняття передачі).</summary>
    public void Remove(EditAssignment assignment);

    /// <summary>
    /// «Мої задачі»: передачі користувача як виконавця у вказаних станах. Лише його рядки; передачі
    /// документів, які користувач не бачить, відсіює викликач (<c>SeesDocumentsOf</c>).
    /// </summary>
    /// <param name="userId">Виконавець.</param>
    /// <param name="statuses">Потрібні стани.</param>
    /// <param name="documentId">Звузити до документа; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<EditAssignmentTaskRow>> ListTasksAsync(
        int userId,
        IReadOnlyCollection<EditAssignmentRecipientStatus> statuses,
        long? documentId,
        CancellationToken ct);
}

/// <summary>Сховище наборів виконавців власника (EA-1, <c>doc.EditorSet*</c>).</summary>
/// <remarks>⛔ Вибірка — завжди за парою <c>(OwnerUserId, Id)</c>: чужий набір — <c>null</c> (T7).</remarks>
public interface IEditorSetStore
{
    /// <summary>Набори власника з членами (відстежувані), за назвою.</summary>
    public Task<IReadOnlyList<EditorSet>> ListByOwnerAsync(int ownerUserId, CancellationToken ct);

    /// <summary>Набір за парою <c>(ownerUserId, setId)</c> з членами; чужий чи відсутній — <c>null</c>.</summary>
    public Task<EditorSet?> FindAsync(int ownerUserId, int setId, CancellationToken ct);

    /// <summary>Скільки наборів у власника (ліміт <see cref="EditorSet.MaxPerOwner"/>).</summary>
    public Task<int> CountByOwnerAsync(int ownerUserId, CancellationToken ct);

    /// <summary>Чи є в власника набір із такою назвою (без урахування регістру — за колацією бази).</summary>
    public Task<bool> NameExistsAsync(int ownerUserId, string name, int? exceptSetId, CancellationToken ct);

    /// <summary>Додає набір у контекст.</summary>
    public void Add(EditorSet set);

    /// <summary>
    /// Видаляє набір із членами і обнуляє <c>AppliedSetId</c> у передачах, що на нього посилались
    /// (<c>ON DELETE SET NULL</c> вимкнено конвенцією бази).
    /// </summary>
    public Task RemoveAsync(EditorSet set, CancellationToken ct);
}
