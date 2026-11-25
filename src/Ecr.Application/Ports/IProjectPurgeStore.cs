using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// ✎ 25.11 (людина: видалити АКТИВНІ проєкти HSE401_2026, TEST_COORD_2026, HSE400_GOLD через інтерфейс): повне
/// видалення проєкту з усіма даними — факти для попереднього перегляду й саме видалення.
/// </summary>
/// <remarks>
/// Окремий порт, а не методи <see cref="IProjectStore"/>: у того десяток тестових підробок, і кожна мусила б
/// реалізувати видалення, якого не торкається (той самий аргумент, що в <see cref="IDocumentDeletionStore"/>).
/// </remarks>
public interface IProjectPurgeStore
{
    /// <summary>
    /// Що буде видалено і що цьому заважає. <c>null</c> — проєкту немає.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="lockForDelete">
    /// <c>true</c> — у транзакції видалення: рядок проєкту під <c>UPDLOCK</c>, щоб паралельна активація/архівація не
    /// проскочила між перевіркою і видаленням.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public Task<ProjectPurgeFacts?> ReadFactsAsync(int projectId, bool lockForDelete, CancellationToken ct);

    /// <summary>
    /// Видаляє всі дані проєкту: кожен документ (робочі дані — тим самим <see cref="IDocumentDeletionStore.DeleteAsync"/>,
    /// що й видалення чернетки, плюс історія погодження, якої в чернетки немає), прогони проєкту, похідні рядки — і
    /// сам проєкт (<see cref="IProjectStore.DeleteAsync"/>). Кличеться всередині транзакції викликача.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="documentIds">Документи проєкту (з <see cref="ReadFactsAsync"/> тієї самої транзакції).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки комірок видалено і скільки задач документів скасовано — для сліду в журналі безпеки.</returns>
    /// <exception cref="Errors.ConcurrencyConflictException">
    /// 409 <c>purgeConcurrentChange</c>: паралельно в проєкті з'явився документ чи інший рядок, якого немає у фактах
    /// (FK 547), — транзакція відкочується, нічого не видалено.
    /// </exception>
    public Task<ProjectPurgeResult> PurgeAsync(int projectId, IReadOnlyList<long> documentIds, CancellationToken ct);
}

/// <summary>Підсумок видалення.</summary>
/// <param name="Cells">Видалено комірок.</param>
/// <param name="CancelledJobs">Скасовано задач (черга — одразу, виконувані — запит скасування) документів проєкту.</param>
public sealed record ProjectPurgeResult(long Cells, int CancelledJobs);

/// <summary>Попередження, що НЕ блокує видалення (рішення — за людиною).</summary>
public static class ProjectPurgeWarnings
{
    /// <summary>✎ 25.11 (R3 C2-g): проєкт золотих тестів HSE400 потрібен інструментам шаблону (<c>tools/hse400</c>).</summary>
    public const string Hse400Tools = "hse400Tools";
}

/// <summary>Документ, що буде видалений.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="BusinessKey">Ключ документа.</param>
/// <param name="TemplateCode">Код шаблону (може бути <c>null</c> для документів до B74).</param>
/// <param name="Cells">Скільки комірок зі значеннями.</param>
public sealed record ProjectPurgeDocument(long DocumentId, string BusinessKey, string? TemplateCode, long Cells);

/// <summary>Документ ІНШОГО проєкту, що читає дані цього (B80: зв'язка документа-джерела).</summary>
/// <param name="DocumentId">Документ-споживач.</param>
/// <param name="BusinessKey">Його ключ.</param>
/// <param name="ProjectCode">Його проєкт.</param>
/// <param name="Reason">Чим залежить: <c>SourceBinding:&lt;код зв'язки&gt;:&lt;рік&gt;</c> або <c>SourceEventMap</c>.</param>
public sealed record ProjectPurgeDependant(long DocumentId, string BusinessKey, string ProjectCode, string Reason);

/// <summary>Кількості рядків, що зникнуть разом із проєктом.</summary>
/// <param name="Cells">Комірки.</param>
/// <param name="CalculationResults">Результати розрахунків.</param>
/// <param name="CalculationRuns">Прогони розрахунків.</param>
/// <param name="CalculationIssues">Проблеми розрахунків.</param>
/// <param name="ApprovalEvents">Події погодження.</param>
/// <param name="ApprovalStates">Стани аркуш × період.</param>
/// <param name="SubmissionSnapshots">Зрізи подання.</param>
/// <param name="Periods">Періоди проєкту.</param>
public sealed record ProjectPurgeCounts(
    long Cells,
    long CalculationResults,
    long CalculationRuns,
    long CalculationIssues,
    long ApprovalEvents,
    long ApprovalStates,
    long SubmissionSnapshots,
    long Periods);

/// <summary>Усе, з чого обробник вирішує, чи проєкт можна видалити з даними, і що показати людині.</summary>
/// <param name="Code">Код проєкту.</param>
/// <param name="Status">Стан.</param>
/// <param name="IsArchiving">Триває архівація.</param>
/// <param name="TemplateVersionId">Основна версія шаблону (для журналу змін).</param>
/// <param name="Documents">Документи проєкту.</param>
/// <param name="Counts">Кількості.</param>
/// <param name="DataMonths">Ключі періодів (<c>yyyyMM</c>), у яких документи проєкту мають екземпляри таблиць.</param>
/// <param name="ArchivedPeriodKeys">Ключі періодів, дані яких перенесено в архів (<c>arc.*</c>, B69).</param>
/// <param name="Dependants">Документи інших проєктів, що читають цей.</param>
/// <param name="EventMaps">Документи ЦЬОГО проєкту з мапами подій джерела (<c>ext.SourceEventMap</c>) — їх прибирають у Sources.</param>
/// <param name="ApprovedMonths">Ключі періодів, у яких є затверджені аркуші (видаляються без повернення — у журнал).</param>
/// <param name="Warnings">Попередження без блокування (<see cref="ProjectPurgeWarnings"/>).</param>
public sealed record ProjectPurgeFacts(
    string Code,
    ProjectStatus Status,
    bool IsArchiving,
    int TemplateVersionId,
    IReadOnlyList<ProjectPurgeDocument> Documents,
    ProjectPurgeCounts Counts,
    IReadOnlyList<int> DataMonths,
    IReadOnlyList<int> ArchivedPeriodKeys,
    IReadOnlyList<ProjectPurgeDependant> Dependants,
    IReadOnlyList<ProjectPurgeDependant> EventMaps,
    IReadOnlyList<int> ApprovedMonths,
    IReadOnlyList<string> Warnings);
