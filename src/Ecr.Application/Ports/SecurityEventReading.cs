namespace Ecr.Application.Ports;

/// <summary>Подія безпеки в журналі, як її бачить читач аудиту (ФВ-5.24, ФВ-6.11).</summary>
/// <param name="ChangedAt">Момент події в UTC.</param>
/// <param name="EventType">Тип: <c>AccessDenied</c>, <c>RoleAssigned</c>, <c>SheetSubmitWarningsAcknowledged</c> тощо.</param>
/// <param name="TargetUserId">Користувач, якого стосується подія; <c>null</c> — не стосується.</param>
/// <param name="TargetRoleId">Роль, якої стосується подія; <c>null</c> — не стосується.</param>
/// <param name="DetailsJson">Деталі події (без PII; для <c>AccessDenied</c> — метод, шаблон маршруту, право/причина).</param>
/// <param name="ChangedByUserId">Хто спричинив подію — <b>UserId</b>, не SID (R-A2, D-86).</param>
/// <param name="ChangedByDisplayName">Ім'я (<c>R-18</c>); <c>null</c> — запису користувача вже немає.</param>
/// <param name="CorrelationId">Ідентифікатор кореляції запиту — той самий, що в тілі відмови.</param>
public sealed record SecurityEventView(
    DateTime ChangedAt,
    string EventType,
    int? TargetUserId,
    int? TargetRoleId,
    string? DetailsJson,
    int ChangedByUserId,
    string? ChangedByDisplayName = null,
    string? CorrelationId = null);

/// <summary>Фільтр журналу подій безпеки.</summary>
/// <remarks>
/// ⛔ Вікно часу — обов'язкове, як і в інших журналах: <c>aud.SecurityEvent</c>
/// лежить на схемі партицій <c>ps_AuditByMonth</c>, запит без меж пішов би по всіх.
/// </remarks>
/// <param name="From">Початок вікна в UTC, включно.</param>
/// <param name="To">Кінець вікна в UTC, виключно.</param>
/// <param name="EventType">Тип події (точний збіг); <c>null</c> — усі.</param>
/// <param name="ChangedByUserId">Автор події — <b>UserId</b>.</param>
public sealed record SecurityEventFilter(
    DateTime From,
    DateTime To,
    string? EventType = null,
    int? ChangedByUserId = null);
