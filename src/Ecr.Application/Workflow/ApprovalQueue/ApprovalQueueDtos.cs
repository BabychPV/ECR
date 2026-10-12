// src/Ecr.Application/Workflow/ApprovalQueue/ApprovalQueueDtos.cs
namespace Ecr.Application.Workflow.ApprovalQueue;

/// <summary>
/// Аркуш, що чекає погодження ПОТОЧНОГО користувача («До погодження», ФВ-12.5).
/// </summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="ProjectCode">Код проєкту документа.</param>
/// <param name="TemplateCode">Код шаблону проєкту.</param>
/// <param name="SheetDefId">Аркуш (для <c>POST /documents/{id}/approve</c>).</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="PeriodKey">Ключ періоду <c>Рік*100+№</c> (для <c>approve</c> і для відкриття документа).</param>
/// <param name="SubmittedAt">Коли подано, UTC.</param>
/// <param name="SubmittedByDisplayName">Хто подав; <c>null</c> — користувача вже немає.</param>
/// <param name="StepOrdinal">Номер поточного кроку маршруту; <c>null</c> — маршруту немає (одноетапне).</param>
/// <param name="StepCount">Скільки кроків у маршруті; <c>null</c> — маршруту немає.</param>
/// <param name="StepRoleCode">Код ролі поточного кроку; <c>null</c> — маршруту немає.</param>
public sealed record ApprovalQueueItemDto(
    long DocumentId,
    string DocumentKey,
    string ProjectCode,
    string TemplateCode,
    int SheetDefId,
    string SheetCode,
    int PeriodKey,
    DateTime SubmittedAt,
    string? SubmittedByDisplayName,
    int? StepOrdinal,
    int? StepCount,
    string? StepRoleCode);

/// <summary>Сторінка черги «До погодження»: найдавніші подання першими.</summary>
/// <param name="Items">Аркуші, які поточний користувач МОЖЕ затвердити зараз.</param>
/// <param name="NextCursor">Непрозорий курсор наступної сторінки; <c>null</c> — кінець черги.</param>
public sealed record ApprovalQueueDto(IReadOnlyList<ApprovalQueueItemDto> Items, string? NextCursor);
