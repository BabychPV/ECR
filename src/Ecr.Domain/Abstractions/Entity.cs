namespace Ecr.Domain.Abstractions;

/// <summary>
/// Сутність із ідентичністю. Рівність — за <typeparamref name="TId"/>,
/// а не за значеннями полів.
/// </summary>
/// <typeparam name="TId">Тип ідентифікатора: <see cref="int"/> для метаданих,
/// <see cref="long"/> для даних документів.</typeparam>
public abstract class Entity<TId> where TId : struct, IEquatable<TId>
{
    /// <summary>Ідентифікатор. Для нових сутностей — значення за замовчуванням.</summary>
    public TId Id { get; protected set; }

    /// <summary>Чи збережена сутність у сховищі.</summary>
    public bool IsPersisted => !Id.Equals(default);

    public override bool Equals(object? obj)
        => obj is Entity<TId> other
           && other.GetType() == GetType()
           && IsPersisted
           && Id.Equals(other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
