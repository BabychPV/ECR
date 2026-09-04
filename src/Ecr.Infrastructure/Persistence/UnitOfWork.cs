using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Клієнт має побачити, ЩО саме розійшлося: «409» без переліку не
            // дає йому нічого, крім пропозиції спробувати ще раз наосліп.
            var conflicts = ex.Entries
                .Select(e => new
                {
                    entity = e.Metadata.DisplayName(),
                    key = string.Join(
                        ",",
                        e.Metadata.FindPrimaryKey()?.Properties
                            .Select(p => $"{p.Name}={e.Property(p.Name).CurrentValue}") ?? []),
                })
                .ToList();

            throw new Application.Errors.ConcurrencyConflictException(
                "ECR-CELL-0409",
                "Дані змінилися після того, як ви їх прочитали.",
                new Dictionary<string, object?> { ["conflicts"] = conflicts });
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ З <c>EnableRetryOnFailure</c> транзакцію треба виконувати цілком
    /// усередині <c>ExecutionStrategy</c>, інакше повтор розірве її посередині.
    /// Тому тут транзакція відкривається напряму, а стратегію повторів має
    /// застосовувати виклик — обгортаючи ВЕСЬ блок роботи.
    /// </remarks>
    public async Task<IAsyncDisposable> BeginTransactionAsync(CancellationToken ct)
        => new TransactionScope(await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Обгортка, чий <c>DisposeAsync</c> відкочує незакомічену транзакцію.
    /// </summary>
    /// <remarks>
    /// «Забули закомітити» має відкотитися, а не лишити відкриту транзакцію:
    /// під RCSI забута транзакція тримає версії рядків у tempdb і псує життя
    /// всій базі, а не тільки своєму запиту.
    /// </remarks>
    private sealed class TransactionScope(IDbContextTransaction transaction) : IAsyncDisposable, IEcrTransaction
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken ct)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Транзакція, яку можна закомітити явно.</summary>
/// <remarks>
/// <see cref="IUnitOfWork.BeginTransactionAsync"/> за контрактом повертає
/// <see cref="IAsyncDisposable"/>. Щоб закомітити, виклик приводить результат
/// до цього інтерфейсу — так контракт лишається незмінним, а «забули
/// закомітити» і далі означає відкат.
/// </remarks>
public interface IEcrTransaction
{
    /// <summary>Фіксує транзакцію.</summary>
    public Task CommitAsync(CancellationToken ct);
}
