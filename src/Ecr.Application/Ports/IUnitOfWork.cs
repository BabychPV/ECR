// src/Ecr.Application/Ports/IUnitOfWork.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): архівація, міграція
/// документа і масовий імпорт виконуються батчами з окремим commit — інакше
/// version store під RCSI росте необмежено.
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
    Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);
}
