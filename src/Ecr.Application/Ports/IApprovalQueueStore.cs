// src/Ecr.Application/Ports/IApprovalQueueStore.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання для черги «До погодження» (ФВ-12.5): кандидати — подані аркуші; рішення, чи може їх затвердити
/// поточний користувач, приймає обробник правилом <c>EditRules.CanApprove</c> (те саме, що в затвердженні).
/// </summary>
public interface IApprovalQueueStore
{
    /// <summary>
    /// Проєкти, у яких взагалі можливе затвердження (не архівні й не в архівації), з кодом шаблону.
    /// Таблиця мала: один запит, без звернень на проєкт.
    /// </summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ApprovalQueueProject>> ListProjectsAsync(CancellationToken ct);

    /// <summary>Усі чинні маршрути погодження з кроками й кодами ролей кроків — одним зверненням.</summary>
    /// <param name="ct">Токен скасування.</param>
    public Task<ApprovalQueueRoutes> ListActiveRoutesAsync(CancellationToken ct);

    /// <summary>
    /// Подані аркуші проєктів <paramref name="projectIds"/> за давністю подання (від найдавнішого), після
    /// <paramref name="after"/>, не більше <paramref name="take"/>.
    /// </summary>
    /// <param name="projectIds">Проєкти, документи яких бачить викликач.</param>
    /// <param name="requireIncludedSheet">
    /// Аркуш має бути у складі документа (<c>IsIncluded</c>) — лише для читача з обмеженнями нижче проєкту
    /// (як <c>RequireSheetVisibleAsync</c>); без обмежень — не перевіряється, як і в затвердженні.
    /// </param>
    /// <param name="after">Курсор (виключно); <c>null</c> — з початку.</param>
    /// <param name="take">Розмір порції.</param>
    /// <param name="participatedDocumentIds">
    /// D-444 (AN-131): документи, відкриті викликачеві наданням учасника, — кандидати ДОДАТКОВО до проєктів
    /// <paramref name="projectIds"/>; порожньо — лише проєкти (попередній предикат).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ApprovalQueueCandidate>> ListSubmittedAsync(
        IReadOnlyCollection<int> projectIds, bool requireIncludedSheet, ApprovalQueueCursor? after, int take,
        IReadOnlyCollection<long> participatedDocumentIds, CancellationToken ct);
}

/// <summary>Проєкт для черги.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="ProjectCode">Код проєкту.</param>
/// <param name="TemplateVersionId">Версія шаблону проєкту.</param>
/// <param name="TemplateCode">Код шаблону.</param>
/// <param name="Status">Статус проєкту.</param>
/// <param name="IsArchiving">Іде архівація.</param>
public sealed record ApprovalQueueProject(
    int ProjectId, string ProjectCode, int TemplateVersionId, string TemplateCode, ProjectStatus Status, bool IsArchiving);

/// <summary>Чинні маршрути з кроками й кодами ролей.</summary>
/// <param name="Routes">Маршрути з кроками.</param>
/// <param name="RoleCodes">Код ролі за її ідентифікатором (ролі кроків).</param>
public sealed record ApprovalQueueRoutes(
    IReadOnlyList<ApprovalRoute> Routes, IReadOnlyDictionary<int, string> RoleCodes);

/// <summary>Подана пара «аркуш × період» документа.</summary>
/// <param name="StateId">Ключ стану (тай-брейк порядку).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="SubmittedAt">Момент подання (UTC); порядок і курсор.</param>
/// <param name="SubmittedByUserId">Хто подав.</param>
/// <param name="SubmittedByDisplayName">Ім'я того, хто подав; <c>null</c> — користувача немає.</param>
/// <param name="CurrentStepId">Збережений крок маршруту.</param>
public sealed record ApprovalQueueCandidate(
    long StateId,
    long DocumentId,
    int ProjectId,
    string DocumentKey,
    int SheetDefId,
    int PeriodKey,
    DateTime SubmittedAt,
    int? SubmittedByUserId,
    string? SubmittedByDisplayName,
    int? CurrentStepId);

/// <summary>Позиція в порядку черги: момент подання (тики UTC) і ключ стану.</summary>
/// <param name="SubmittedAtTicks">Тики моменту подання.</param>
/// <param name="StateId">Ключ стану.</param>
public sealed record ApprovalQueueCursor(long SubmittedAtTicks, long StateId);
