using Ecr.Domain.Entities.Workflow;

namespace Ecr.Application.Ports;

/// <summary>Видалення документа-чернетки (право <c>Document.Delete</c>).</summary>
/// <remarks>
/// Окремий порт, а не два методи в <see cref="IDocumentStore"/>: у того десяток
/// тестових підробок, і кожна мусила б реалізувати видалення, якого не торкається.
/// Обидва методи кличуться всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.
/// </remarks>
public interface IDocumentDeletionStore
{
    /// <summary>
    /// Читає стани аркушів документа під блокуванням діапазону до кінця транзакції,
    /// щоб паралельне подання не встигло між перевіркою «чернетка» і видаленням.
    /// </summary>
    public Task<DocumentWorkflowFacts> LockWorkflowFactsAsync(long documentId, CancellationToken ct);

    /// <summary>Видаляє документ і всі його робочі дані.</summary>
    /// <returns>Скільки комірок видалено — для сліду в журналі безпеки.</returns>
    public Task<int> DeleteAsync(long documentId, CancellationToken ct);
}

/// <summary>Усе, з чого домен вирішує, чи документ — чернетка.</summary>
/// <param name="SheetStates">Стани аркуш × період.</param>
/// <param name="HasWorkflowHistory">Є події погодження або зрізи подання.</param>
public sealed record DocumentWorkflowFacts(
    IReadOnlyCollection<ApprovalState> SheetStates, bool HasWorkflowHistory);
