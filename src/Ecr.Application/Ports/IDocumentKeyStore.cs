using Ecr.Domain.Entities.Documents;

namespace Ecr.Application.Ports;

/// <summary>Зміна бізнес-ключа документа (ФВ-3.9, право <c>Document.ChangeKey</c>).</summary>
/// <remarks>
/// Окремий порт з тієї ж причини, що й <see cref="IDocumentDeletionStore"/>: у
/// <see cref="IDocumentStore"/> десяток тестових підробок. Обидва методи кличуться
/// всередині <see cref="IUnitOfWork.ExecuteInTransactionAsync"/>.
/// </remarks>
public interface IDocumentKeyStore
{
    /// <summary>Документ під <c>UPDLOCK</c> для зміни; <c>null</c> — не існує.</summary>
    public Task<Document?> FindForUpdateAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// Чи зайнятий ключ іншим документом проєкту. Читає під <c>UPDLOCK, HOLDLOCK</c>:
    /// паралельна зміна на той самий ключ чекає кінця транзакції.
    /// </summary>
    public Task<bool> IsKeyTakenAsync(int projectId, string businessKey, long exceptDocumentId, CancellationToken ct);
}
