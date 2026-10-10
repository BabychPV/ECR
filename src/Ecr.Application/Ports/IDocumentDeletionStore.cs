using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>Видалення документа-чернетки (право <c>Document.Delete</c>).</summary>
/// <remarks>
/// Окремий порт, а не два методи в <see cref="IDocumentStore"/>: у того десяток
/// тестових підробок, і кожна мусила б реалізувати видалення, якого не торкається.
/// Усі методи кличуться всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.
/// </remarks>
public interface IDocumentDeletionStore
{
    /// <summary>
    /// Читає стани аркушів документа під блокуванням діапазону до кінця транзакції,
    /// щоб паралельне подання не встигло між перевіркою «чернетка» і видаленням.
    /// </summary>
    public Task<DocumentWorkflowFacts> LockWorkflowFactsAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Стан проєкту документа і періоди, у яких документ має дані (комірки чи значення PI за
    /// вікном рядка), — періоди під <c>UPDLOCK</c> до кінця транзакції.
    /// </summary>
    /// <remarks>
    /// ⛔ R9-F3 / F3-01: видалення стирає дані за ВСІМА періодами документа, тож «закритий період
    /// блокує всіх» (02c A7) і архів проєкту діють і тут. Блокування — той самий <c>UPDLOCK</c>,
    /// що бере <c>ClosedPeriodGuard</c>: паралельне закриття періоду не проскочить між
    /// перевіркою й видаленням. Кличеться ДО <see cref="LockWorkflowFactsAsync"/> — порядок
    /// «період → стан аркуша», як у <c>Recall</c>/<c>Reopen</c>.
    /// </remarks>
    public Task<DocumentFreezeFacts> LockFreezeFactsAsync(long documentId, CancellationToken ct);

    /// <summary>Видаляє документ і всі його робочі дані.</summary>
    /// <returns>Скільки комірок видалено — для сліду в журналі безпеки.</returns>
    public Task<int> DeleteAsync(long documentId, CancellationToken ct);
}

/// <summary>Усе, з чого домен вирішує, чи документ — чернетка.</summary>
/// <param name="SheetStates">Стани аркуш × період.</param>
/// <param name="HasWorkflowHistory">Є події погодження або зрізи подання.</param>
public sealed record DocumentWorkflowFacts(
    IReadOnlyCollection<ApprovalState> SheetStates, bool HasWorkflowHistory);

/// <summary>Усе, з чого обробник вирішує, чи дані документа ще не заморожено.</summary>
/// <param name="ProjectStatus">Стан проєкту документа.</param>
/// <param name="IsArchiving">Чи триває архівація проєкту.</param>
/// <param name="PeriodsWithData">Періоди проєкту, у яких документ має дані.</param>
public sealed record DocumentFreezeFacts(
    ProjectStatus ProjectStatus, bool IsArchiving, IReadOnlyList<Period> PeriodsWithData);
