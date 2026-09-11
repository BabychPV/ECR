// src/Ecr.Application/Ports/IUnitOfWork.cs
namespace Ecr.Application.Ports;

/// <summary>
/// Транзакційна межа. Довгі транзакції заборонені (D-29): архівація, міграція
/// документа і масовий імпорт виконуються батчами з окремим commit — інакше
/// version store під RCSI росте необмежено.
/// </summary>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct);
    public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct);

    /// <summary>
    /// Виконує <paramref name="operation"/> в одній транзакції: коміт — лише
    /// якщо вона завершилась без винятку; будь-який виняток — повний відкат.
    /// </summary>
    /// <remarks>
    /// ⛔ Q-243. Реальний DbContext застосунку піднятий з
    /// <c>EnableRetryOnFailure</c> (`DependencyInjection.cs`), і EF Core з
    /// таким увімкненням вимагає, щоб ЗАКРИТА транзакція (Begin + вся робота
    /// + Commit) виконувалась ОДНИМ замиканням усередині
    /// <c>Database.CreateExecutionStrategy().ExecuteAsync</c> — інакше кидає
    /// одразу на першій-ліпшій EF-операції, що йде через
    /// <c>ExecuteUpdateAsync</c>/<c>SaveChangesAsync</c> ПІСЛЯ ручного
    /// <c>BeginTransaction</c> (<c>InvalidOperationException</c>: «does not
    /// support user-initiated transactions»). Виміряно прогоном
    /// <c>Ecr.Scenarios.Tests</c> проти реального піднятого <c>Ecr.Api</c>:
    /// перший же варіант фіксу (окремі <c>BeginTransactionAsync</c>/
    /// <c>CommitAsync</c>, а між ними — окремі виклики
    /// <c>RowStore.TouchRowsAsync</c> через <c>ExecuteUpdateAsync</c>) впав
    /// РІВНО на цьому винятку — <c>UnitOfWorkTests</c> (Infrastructure.Tests)
    /// цього не зловив, бо його <c>EcrDbContext</c> ретраю не має. Цей метод
    /// — єдиний спосіб дати виклику (`PatchCellsHandler.PersistChangesAsync`)
    /// відкрити транзакцію, зробити кілька кроків через РІЗНІ порти і
    /// закомітити РІВНО один раз, лишаючись сумісним з реальним, ретраюваним
    /// DbContext.
    /// </remarks>
    public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> operation, CancellationToken ct);
}
