// src/Ecr.Application/Ports/IApprovalQueueStore.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

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

    /// <summary>
    /// ✎ D-449 («Повернуто на доопрацювання» у «My tasks»): відхилені погоджувачем (<c>Rejected</c>) аркуші
    /// документів, де <paramref name="userId"/> — власник (у проєктах <paramref name="ownedProjectIds"/>), або
    /// документів <paramref name="participatedDocumentIds"/>, від найдавнішого повернення, після <paramref name="after"/>,
    /// не більше <paramref name="take"/>.
    /// </summary>
    /// <remarks>
    /// Окремого порту немає навмисно: та сама пара «аркуш × період» з <c>wf.ApprovalState</c>, ті самі проєкти
    /// (<see cref="ListProjectsAsync"/> — архівний проєкт повторно не подається) і той самий keyset. Чия це задача й чи
    /// видно аркуш — вирішує обробник (<c>ListMyReturnedSheetsHandler</c>), сховище лише звужує кандидатів.
    /// </remarks>
    /// <param name="userId">Викликач (власник — <c>Document.CreatedByUserId</c>, D-443: власника можна передати).</param>
    /// <param name="ownedProjectIds">Проєкти, документи яких викликач бачить за ролями.</param>
    /// <param name="requireIncludedSheet">
    /// Аркуш має бути у складі документа (<c>IsIncluded</c>) — для читача з обмеженнями нижче проєкту, як у черзі.
    /// </param>
    /// <param name="after">Курсор (виключно); <c>null</c> — з початку.</param>
    /// <param name="take">Розмір порції.</param>
    /// <param name="participatedDocumentIds">Документи, де викликач має діюче надання учасника.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<ReturnedSheetCandidate>> ListReturnedAsync(
        int userId,
        IReadOnlyCollection<int> ownedProjectIds,
        bool requireIncludedSheet,
        ReturnedSheetsCursor? after,
        int take,
        IReadOnlyCollection<long> participatedDocumentIds,
        CancellationToken ct);
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

/// <summary>Відхилена пара «аркуш × період» документа.</summary>
/// <param name="StateId">Ключ стану (тай-брейк порядку).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentKey">Бізнес-ключ документа.</param>
/// <param name="DocumentNameL10n">Назва документа; <c>null</c> — не задана.</param>
/// <param name="OwnerUserId">Власник документа.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="ReturnedAt">Коли повернуто (UTC); порядок і курсор.</param>
/// <param name="ReturnedByDisplayName">Хто повернув; <c>null</c> — користувача немає.</param>
/// <param name="Reason">Причина повернення.</param>
public sealed record ReturnedSheetCandidate(
    long StateId,
    long DocumentId,
    int ProjectId,
    string DocumentKey,
    LocalizedText? DocumentNameL10n,
    int OwnerUserId,
    int SheetDefId,
    int PeriodKey,
    DateTime ReturnedAt,
    string? ReturnedByDisplayName,
    string Reason);

/// <summary>Позиція в порядку блоку: момент повернення (тики UTC) і ключ стану.</summary>
/// <param name="ReturnedAtTicks">Тики моменту повернення.</param>
/// <param name="StateId">Ключ стану.</param>
public sealed record ReturnedSheetsCursor(long ReturnedAtTicks, long StateId);
