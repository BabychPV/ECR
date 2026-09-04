using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Одиниця роботи поверх <see cref="EcrDbContext"/>.
/// </summary>
/// <remarks>
/// ⚠ Довгі транзакції заборонені (<c>D-29</c>): архівація, міграція документа
/// і масовий імпорт ідуть батчами з окремим commit — інакше version store під
/// RCSI росте необмежено.
///
/// Постановка перерахунку в чергу відбувається <b>поза</b> транзакцією запису:
/// інакше воркер починає читати рядки, яких ще не видно, і отримує або старі
/// значення, або блокування на піку останнього дня періоду.
/// </remarks>
public sealed class UnitOfWork(EcrDbContext db) : IUnitOfWork
{
    /// <inheritdoc />
    public Task<int> SaveChangesAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: делегувати db.SaveChangesAsync(ct). DbUpdateConcurrencyException перетворити " +
            "на ConcurrencyConflictException із переліком зачеплених рядків — клієнт має " +
            "побачити, ЩО саме розійшлося, а не просто 409.");

    /// <inheritdoc />
    public Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: db.Database.BeginTransactionAsync(ct); повернути обгортку, чий DisposeAsync " +
            "робить Rollback, якщо Commit не викликали — «забули закомітити» має відкотитися, " +
            "а не залишити відкриту транзакцію.\n" +
            "⚠ З EnableRetryOnFailure транзакцію треба виконувати цілком усередині " +
            "ExecutionStrategy, інакше повтор розірве її посередині.\n" +
            "⚠ Усередину транзакції не класти нічого зовнішнього: ані HTTP, ані постановку " +
            "задачі в чергу.");
}
