// src/Ecr.Application/Ports/IEditAssignmentManagementStore.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Сховище керування передачами місяця (EA-C): читання й зміна <c>doc.EditAssignment</c> із
/// блокуванням рядка для переходів станів.
/// </summary>
/// <remarks>
/// ⚠ НЕ те, що читає рішення про запис комірки (<c>AccessDecisionService</c>, EA-2): це порт
/// ЛЮДСЬКИХ дій (передати, заповнити, валідувати, повернути, зняти).
/// ⛔ Усі методи «ForUpdate» — лише всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>:
/// блокування <c>UPDLOCK</c> тримається до коміту, і саме воно робить «перевірив стан — змінив»
/// атомарним (і серіалізує «Заповнено» з повторною перевіркою запису під гейтом, EA-2).
/// </remarks>
public interface IEditAssignmentManagementStore
{
    /// <summary>Документ як посилання для авторизації; <c>null</c> — документа немає.</summary>
    public Task<EditAssignmentDocumentRef?> FindDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Блокує рядок документа (<c>UPDLOCK</c>) до коміту транзакції; <c>false</c> — документа вже немає.
    /// Створення/зміна передачі йде під цим блокуванням: видалення документа (воно бере те саме)
    /// не може проскочити між перевіркою і вставкою (аудит P2).
    /// </summary>
    public Task<bool> LockDocumentAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Блокує рядки виконавців передачі (<c>UPDLOCK</c>) до коміту. Викликається одразу після взяття рядка
    /// передачі: порядок блокувань structure → header → sheet-edit → assignment → recipients (EA-B).
    /// </summary>
    public Task LockRecipientsAsync(long assignmentId, CancellationToken ct);

    /// <summary>
    /// Обмежує очікування блокувань у поточній транзакції (<c>SET LOCK_TIMEOUT</c>): вичерпане очікування
    /// <c>UnitOfWork</c> перекладає в <c>409 ECR-DOC-4091</c> замість висіння до <c>CommandTimeout</c>.
    /// </summary>
    public Task LimitLockWaitAsync(CancellationToken ct);

    /// <summary>Передача документа за ключем зрізу — з блокуванням, відстежується; <c>null</c>, якщо немає.</summary>
    public Task<EditAssignment?> FindBySliceKeyForUpdateAsync(long documentId, string sliceKey, CancellationToken ct);

    /// <summary>Користувачі за ідентифікаторами (нескасовані й вимкнені); відсутні в результаті не повертаються.</summary>
    public Task<IReadOnlyDictionary<int, EditAssignmentUserRef>> GetUsersAsync(
        IReadOnlyCollection<int> userIds, CancellationToken ct);

    /// <summary>Пошук активних користувачів за частиною імені/логіну; без email і SID.</summary>
    public Task<IReadOnlyList<EditAssignmentUserRef>> SearchCandidatesAsync(string query, int take, CancellationToken ct);

    /// <summary>
    /// Передачі, пов'язані з користувачем: як виконавця (рядки виконавця) і як власника
    /// (делегатор або автор документа). Лише рядки, що стосуються самого користувача.
    /// </summary>
    public Task<IReadOnlyList<MyEditAssignmentRow>> ListMineAsync(
        int userId, long? documentId, CancellationToken ct);
}

/// <summary>Документ для перевірки прав керування передачами.</summary>
/// <param name="Id">Документ.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="BusinessKey">Бізнес-ключ (для листів і «Моїх задач»).</param>
/// <param name="CreatedByUserId">Автор документа («власник»).</param>
/// <param name="ProjectStatus">Стан проєкту (архів — лише читання).</param>
/// <param name="IsArchiving">Проєкт архівується.</param>
public sealed record EditAssignmentDocumentRef(
    long Id, int ProjectId, string BusinessKey, int CreatedByUserId, ProjectStatus ProjectStatus, bool IsArchiving);

/// <summary>Користувач для відображення в передачі (без SID і без даних автентифікації).</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="DisplayName">Відображуване ім'я.</param>
/// <param name="Email">Адреса для листа; може бракувати.</param>
/// <param name="IsActive">Не вимкнений.</param>
public sealed record EditAssignmentUserRef(int Id, string DisplayName, string? Email, bool IsActive);

/// <summary>Рядок «Моїх задач»: передача з позиції користувача.</summary>
/// <param name="AssignmentId">Передача.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="ProjectId">Проєкт (для перевірки видимості).</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="SheetCode">Код аркуша зрізу.</param>
/// <param name="TableCode">Код таблиці зрізу.</param>
/// <param name="MonthNumber">Місяць зрізу.</param>
/// <param name="PeriodKey">Період зрізу.</param>
/// <param name="IsExclusive">Ексклюзивна.</param>
/// <param name="Note">Інструкція виконавцям.</param>
/// <param name="DelegatedByUserId">Хто передав.</param>
/// <param name="DelegatedByDisplayName">Ім'я того, хто передав.</param>
/// <param name="ValidationStatus">Валідація зрізу.</param>
/// <param name="MyStatus">Стан самого користувача як виконавця; <c>null</c> — не виконавець.</param>
/// <param name="MyReturnReason">Причина повернення (лише для себе).</param>
/// <param name="MyStatusChangedAt">Коли змінився власний стан.</param>
/// <param name="IsOwnerOf">Користувач — делегатор або автор документа.</param>
/// <param name="RecipientCount">Кількість виконавців (без імен).</param>
/// <param name="FilledCount">Скільки виконавців заповнили.</param>
/// <param name="Version">Версія передачі (для <c>baseVersion</c>).</param>
public sealed record MyEditAssignmentRow(
    long AssignmentId,
    long DocumentId,
    int ProjectId,
    string DocumentKey,
    string? SheetCode,
    string? TableCode,
    byte? MonthNumber,
    int? PeriodKey,
    bool IsExclusive,
    string? Note,
    int DelegatedByUserId,
    string DelegatedByDisplayName,
    EditAssignmentValidationStatus ValidationStatus,
    EditAssignmentRecipientStatus? MyStatus,
    string? MyReturnReason,
    DateTime? MyStatusChangedAt,
    bool IsOwnerOf,
    int RecipientCount,
    int FilledCount,
    byte[] Version);
