// src/Ecr.Application/Ports/ISourceApprovalChangeTrigger.cs
namespace Ecr.Application.Ports;

/// <summary>Який перехід аркуша-джерела змінив те, що бачать залежні документи.</summary>
public enum SourceApprovalChange
{
    /// <summary>Остаточне затвердження (останній крок маршруту): аркуш × період став <c>Approved</c>.</summary>
    Approved,

    /// <summary>Повернення в роботу ЗІ стану <c>Approved</c>.</summary>
    Reopened,
}

/// <summary>Що треба знати про документ-джерело ДО його видалення (після видалення його вже немає).</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="RegistryDefId">Довідник зв'язки.</param>
/// <param name="Years">Роки, у яких документ мав екземпляри таблиць (оригінал аркуша).</param>
public sealed record SourceDocumentDeletion(long DocumentId, int RegistryDefId, IReadOnlyList<int> Years);

/// <summary>
/// B80 (N2a, RW.2 T-A/T-B/T-C): за зв'язкою джерела в стані <c>ApprovedOnly</c> залежні бачать лише затверджені
/// місяці джерела, тож перерахунок залежних ставлять три дії: остаточне затвердження аркуша джерела, повернення
/// в роботу із затвердженого і видалення документа-джерела.
/// </summary>
/// <remarks>
/// ⛔ Викликати лише ПІСЛЯ коміту дії. Реалізація не кидає (крім скасування): збій постановки — у журнал, дію
/// користувача він не валить. За стану <c>Any</c> і за вимкненим бітом <c>RecalculateDependents</c> — нічого.
/// </remarks>
public interface ISourceApprovalChangeTrigger
{
    /// <summary>Аркуш (оригінал) документа × період остаточно затверджено або повернуто із затвердженого.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період аркуша.</param>
    /// <param name="change">Перехід.</param>
    /// <param name="userId">Хто діяв.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task OnSourceSheetApprovalChangedAsync(
        long documentId, int sheetDefId, int periodKey, SourceApprovalChange change, int? userId, CancellationToken ct);

    /// <summary>ДО видалення: чи документ — джерело зв'язки <c>ApprovedOnly</c>, і які його роки; інакше <c>null</c>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<SourceDocumentDeletion?> PrepareDocumentDeletionAsync(long documentId, CancellationToken ct);

    /// <summary>ПІСЛЯ коміту видалення: ставить перерахунок залежних за роками документа.</summary>
    /// <param name="deletion">Результат <see cref="PrepareDocumentDeletionAsync"/>.</param>
    /// <param name="userId">Хто видалив.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task OnSourceDocumentDeletedAsync(SourceDocumentDeletion deletion, int? userId, CancellationToken ct);
}
