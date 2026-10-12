// src/Ecr.Application/Documents/EditAssignments/EditAssignmentDtos.cs
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.EditAssignments;

/// <summary>Зріз передачі: усі селектори необов'язкові; жодного — «весь документ».</summary>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="TableCode">Код таблиці (потребує <paramref name="SheetCode"/>).</param>
/// <param name="MonthNumber">Місяць 1..12 для місячних колонок.</param>
/// <param name="PeriodKey">Ключ періоду <c>Рік*100+№</c>.</param>
public sealed record EditAssignmentSliceDto(string? SheetCode, string? TableCode, byte? MonthNumber, int? PeriodKey);

/// <summary>Чи бачить виконавець документ (і аркуш передачі) за прямими призначеннями ролей (RC20 D-2).</summary>
public enum RecipientVisibility
{
    /// <summary>Не обчислювалось або невідомо (групи AD чужої людини, вимкнений обліковий запис, стеля).</summary>
    Unknown = 0,

    /// <summary>Бачить документ (і аркуш передачі).</summary>
    Visible = 1,

    /// <summary>Не бачить документ чи аркуш: передача для нього без ефекту.</summary>
    NotVisible = 2,
}

/// <summary>Виконавець у передачі.</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="DisplayName">Ім'я.</param>
/// <param name="Status"><c>Assigned</c> | <c>Filled</c> | <c>Returned</c>.</param>
/// <param name="FilledAt">Коли заповнив.</param>
/// <param name="ReturnedAt">Коли повернули.</param>
/// <param name="ReturnReason">Причина повернення.</param>
/// <param name="ReturnCount">Скільки разів повертали.</param>
/// <param name="IsActive">Обліковий запис не вимкнений.</param>
/// <param name="Visibility">
/// Чи бачить виконавець документ (і аркуш передачі) за ПРЯМИМИ призначеннями ролей:
/// <c>Visible</c> | <c>NotVisible</c> | <c>Unknown</c> (групи AD чужої людини невідомі; у відповідях, що не є
/// переліком передач, не обчислюється). RC20 D-2.
/// </param>
public sealed record EditAssignmentRecipientDto(
    int UserId,
    string DisplayName,
    string Status,
    DateTime? FilledAt,
    DateTime? ReturnedAt,
    string? ReturnReason,
    int ReturnCount,
    bool IsActive,
    RecipientVisibility Visibility = RecipientVisibility.Unknown);

/// <summary>Передача зрізу з виконавцями.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Slice">Зріз.</param>
/// <param name="IsExclusive">Інші за ролями не можуть писати в зріз.</param>
/// <param name="Status">Похідний: <c>Open</c> | <c>Assigned</c> | <c>InProgress</c> | <c>Filled</c> | <c>Returned</c> | <c>Validated</c>.</param>
/// <param name="ValidatedAt">Коли валідовано.</param>
/// <param name="ValidatedByDisplayName">Хто валідував.</param>
/// <param name="DelegatedByUserId">Чиїми правами обмежено передачу.</param>
/// <param name="DelegatedByDisplayName">Ім'я делегатора.</param>
/// <param name="Note">Інструкція виконавцям.</param>
/// <param name="AppliedSetId">Набір, з якого взято склад.</param>
/// <param name="AppliedSetName">Знімок назви набору.</param>
/// <param name="Recipients">Виконавці.</param>
/// <param name="ModifiedAt">Коли змінено.</param>
/// <param name="Version">Версія для <c>baseVersion</c>.</param>
/// <param name="ExpiresAt">Термін дії передачі (UTC); <c>null</c> — безстроково.</param>
public sealed record EditAssignmentDto(
    long Id,
    EditAssignmentSliceDto Slice,
    bool IsExclusive,
    string Status,
    DateTime? ValidatedAt,
    string? ValidatedByDisplayName,
    int DelegatedByUserId,
    string DelegatedByDisplayName,
    string? Note,
    int? AppliedSetId,
    string? AppliedSetName,
    IReadOnlyList<EditAssignmentRecipientDto> Recipients,
    DateTime ModifiedAt,
    string Version,
    DateTime? ExpiresAt = null);

/// <summary>Перелік передач документа для менеджера.</summary>
/// <param name="OwnerDisplayName">Автор документа.</param>
/// <param name="CanManage">Викликач може керувати (завжди <c>true</c> для 200).</param>
/// <param name="Assignments">Передачі (без аркушів, схованих від викликача).</param>
public sealed record EditAssignmentListDto(
    string OwnerDisplayName, bool CanManage, IReadOnlyList<EditAssignmentDto> Assignments);

/// <summary>Тіло створення/зміни передачі (upsert за зрізом).</summary>
/// <param name="Slice">Зріз.</param>
/// <param name="RecipientUserIds">Виконавці (1..100).</param>
/// <param name="IsExclusive">Закрити зріз іншим за ролями; за замовчуванням <c>true</c>.</param>
/// <param name="Note">Інструкція (≤ 500).</param>
/// <param name="AppliedSetId">Набір, з якого взято склад (знімок назви беремо на сервері).</param>
/// <param name="BaseVersion">Обов'язкова для зміни наявної передачі.</param>
public sealed record UpsertEditAssignmentRequest(
    EditAssignmentSliceDto Slice,
    IReadOnlyList<int> RecipientUserIds,
    bool IsExclusive = true,
    string? Note = null,
    int? AppliedSetId = null,
    string? BaseVersion = null)
{
    private DateTime? _expiresAt;
    private bool _expiresAtSpecified;

    /// <summary>
    /// Термін дії передачі, UTC: у майбутньому й не пізніше ніж через 366 діб. PATCH-семантика: ПОЛЕ ВІДСУТНЄ в
    /// тілі — термін наявної передачі не змінюється (нова — без терміну); явний <c>null</c> — термін знімається;
    /// значення — замінює.
    /// </summary>
    /// <remarks>
    /// ⚠ Не параметр конструктора навмисно: <c>System.Text.Json</c> викликає init-аксесор лише для присутнього
    /// в JSON поля (у тому числі зі значенням <c>null</c>), і тому розрізняє «відсутнє» й «null» —
    /// <see cref="ExpiresAtSpecified"/>.
    /// </remarks>
    public DateTime? ExpiresAt
    {
        get => _expiresAt;
        init
        {
            _expiresAt = value;
            _expiresAtSpecified = true;
        }
    }

    /// <summary>Чи було поле <see cref="ExpiresAt"/> у тілі запиту (навіть зі значенням <c>null</c>).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ExpiresAtSpecified => _expiresAtSpecified;
}

/// <summary>Тіло валідації.</summary>
/// <param name="BaseVersion">Версія передачі, яку бачив власник.</param>
public sealed record ValidateEditAssignmentRequest(string? BaseVersion);

/// <summary>Тіло повернення на доопрацювання.</summary>
/// <param name="UserIds">Кого повернути; порожньо — усіх «Заповнено».</param>
/// <param name="Reason">Причина 1..500.</param>
/// <param name="BaseVersion">Версія передачі, яку бачив власник.</param>
public sealed record ReturnEditAssignmentRequest(
    IReadOnlyList<int>? UserIds, string Reason, string? BaseVersion);

/// <summary>Кандидат у виконавці (без email і SID).</summary>
/// <param name="UserId">Користувач.</param>
/// <param name="DisplayName">Ім'я.</param>
public sealed record EditAssignmentCandidateDto(int UserId, string DisplayName);

/// <summary>Моя задача як виконавця або очікування валідації як власника.</summary>
/// <param name="AssignmentId">Передача.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="Slice">Зріз.</param>
/// <param name="Status">Мій стан виконавця (<c>Assigned</c>/<c>Returned</c>/<c>Filled</c>) або похідний стан зрізу для власника.</param>
/// <param name="ReturnReason">Причина повернення (лише мені).</param>
/// <param name="Note">Інструкція власника.</param>
/// <param name="DelegatedByDisplayName">Хто передав.</param>
/// <param name="RecipientCount">Скільки виконавців (без імен).</param>
/// <param name="FilledCount">Скільки заповнили.</param>
/// <param name="StatusChangedAt">Коли змінився стан.</param>
/// <param name="Version">Версія для <c>baseVersion</c>.</param>
public sealed record MyEditAssignmentDto(
    long AssignmentId,
    long DocumentId,
    string DocumentKey,
    EditAssignmentSliceDto Slice,
    string Status,
    string? ReturnReason,
    string? Note,
    string DelegatedByDisplayName,
    int RecipientCount,
    int FilledCount,
    DateTime? StatusChangedAt,
    string Version);

/// <summary>Відповідь «Мої задачі» за передачами.</summary>
/// <param name="AssignedToMe">Виконати: <c>Assigned</c> і <c>Returned</c>.</param>
/// <param name="AwaitingMyValidation">Усі виконавці заповнили — чекає валідації мною.</param>
public sealed record MyEditAssignmentsDto(
    IReadOnlyList<MyEditAssignmentDto> AssignedToMe,
    IReadOnlyList<MyEditAssignmentDto> AwaitingMyValidation);

/// <summary>Версія передачі: <c>baseVersion</c> клієнта звіряється з нею.</summary>
/// <remarks>
/// ⚠ Версія — <c>RowVersion</c> рядка передачі. Кожен перехід (заповнено, повернуто, валідовано,
/// зміна складу) викликає <c>EditAssignment.Touch</c>, тож рядок передачі змінюється разом із рядком
/// виконавця; окремої версії виконавців не потрібно.
/// </remarks>
internal static class EditAssignmentVersion
{
    /// <summary>Версія за значенням <c>RowVersion</c>.</summary>
    public static string OfBytes(byte[] rowVersion)
    {
        ArgumentNullException.ThrowIfNull(rowVersion);
        return Convert.ToHexString(rowVersion);
    }

    /// <summary>Версія передачі.</summary>
    public static string Of(EditAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return OfBytes(assignment.RowVersion);
    }
    /// <summary>Похідний стан зрізу для UI.</summary>
    public static string StatusOf(EditAssignment a)
    {
        if (a.Recipients.Count == 0)
        {
            return "Open";
        }

        if (a.ValidationStatus == EditAssignmentValidationStatus.Validated)
        {
            return "Validated";
        }

        if (a.Recipients.Any(r => r.Status == EditAssignmentRecipientStatus.Returned))
        {
            return "Returned";
        }

        var filled = a.Recipients.Count(r => r.Status == EditAssignmentRecipientStatus.Filled);
        if (filled == a.Recipients.Count)
        {
            return "Filled";
        }

        return filled > 0 ? "InProgress" : "Assigned";
    }
}
