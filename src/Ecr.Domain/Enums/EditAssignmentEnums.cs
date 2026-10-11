// src/Ecr.Domain/Enums/EditAssignmentEnums.cs
namespace Ecr.Domain.Enums;

/// <summary>
/// Стан виконавця в передачі місяця (<c>doc.EditAssignmentRecipient.Status</c>, EA-1).
/// Цикл: <c>Assigned → Filled → Returned → Filled → …</c>; «Валідовано» — стан зрізу
/// (<see cref="EditAssignmentValidationStatus"/>), а не виконавця.
/// </summary>
/// <remarks>
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень; змінювати й переставляти не можна.
/// </remarks>
public enum EditAssignmentRecipientStatus : byte
{
    /// <summary>Передано: виконавець заповнює комірки зрізу.</summary>
    Assigned = 0,

    /// <summary>Заповнено: виконавець позначив зріз готовим і більше не пише, доки власник не поверне.</summary>
    Filled = 1,

    /// <summary>Повернуто власником на доопрацювання (з обов'язковою причиною): виконавець пише знову.</summary>
    Returned = 2,
}

/// <summary>
/// Стан валідації зрізу власником (<c>doc.EditAssignment.ValidationStatus</c>, EA-1).
/// </summary>
/// <remarks>
/// ⚠ «Валідовано» ≠ затвердження аркуша (<c>wf.ApprovalState</c>): це різні життєві цикли.
/// ⛔ Числа — значення колонки <c>tinyint</c> і CHECK-обмежень.
/// </remarks>
public enum EditAssignmentValidationStatus : byte
{
    /// <summary>Не валідовано (початковий стан, а також після повернення чи додавання виконавця).</summary>
    NotValidated = 0,

    /// <summary>Валідовано: усі виконавці зрізу «Заповнено», власник підтвердив.</summary>
    Validated = 1,
}
