namespace Ecr.Application.Ports;

/// <summary>Подія переходу стану аркуша (<c>wf.ApprovalEvent</c>) у журналі аудиту.</summary>
/// <param name="ChangedAt">Момент дії в UTC.</param>
/// <param name="Action">Дія: <c>Submit</c>, <c>Approve</c>, <c>ApproveStep</c>, <c>Reject</c>, <c>Reopen</c>, <c>Recall</c>.</param>
/// <param name="FromStatus">Стан до дії.</param>
/// <param name="ToStatus">Стан після дії.</param>
/// <param name="Comment">Причина/коментар (звичайний текст); <c>null</c> — дія без коментаря.</param>
/// <param name="ChangedByUserId">Хто виконав — <b>UserId</b>; <c>null</c> — системний перехід.</param>
/// <param name="ChangedByDisplayName">Ім'я автора; <c>null</c> — система або запису користувача вже немає.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentBusinessKey">Бізнес-ключ документа; <c>null</c> — документа вже немає.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша; <c>null</c> — визначення вже немає.</param>
/// <param name="PeriodKey">Звітний період.</param>
/// <param name="StepOrdinal">Крок маршруту, якщо маршрут є.</param>
public sealed record WorkflowEventView(
    DateTime ChangedAt,
    string Action,
    string FromStatus,
    string ToStatus,
    string? Comment,
    int? ChangedByUserId,
    string? ChangedByDisplayName,
    long DocumentId,
    string? DocumentBusinessKey,
    int SheetDefId,
    string? SheetCode,
    int PeriodKey,
    int? StepOrdinal);

/// <summary>Фільтр журналу подій погодження; вікно часу обов'язкове, як у решти журналів.</summary>
/// <param name="From">Початок вікна в UTC, включно.</param>
/// <param name="To">Кінець вікна в UTC, виключно.</param>
/// <param name="Action">Дія (точний збіг); <c>null</c> — усі.</param>
/// <param name="ChangedByUserId">Автор дії — <b>UserId</b>.</param>
/// <param name="DocumentId">Документ; <c>null</c> — усі.</param>
/// <param name="Sort">Порядок сторінок.</param>
public sealed record WorkflowEventFilter(
    DateTime From,
    DateTime To,
    string? Action = null,
    int? ChangedByUserId = null,
    long? DocumentId = null,
    AuditSort Sort = AuditSort.ChangedAtDesc);
