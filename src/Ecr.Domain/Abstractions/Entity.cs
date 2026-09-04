namespace Ecr.Domain.Abstractions;

/// <summary>
/// Сутність із ідентичністю. Рівність — за <typeparamref name="TId"/>,
/// а не за значеннями полів.
/// </summary>
/// <typeparam name="TId">Тип ідентифікатора: <see cref="int"/> для метаданих,
/// <see cref="long"/> для даних документів, <see cref="string"/> там, де
/// ключем є код (<c>sec.Permission.Code</c>).</typeparam>
public abstract class Entity<TId> where TId : IEquatable<TId>
{
    /// <summary>Ідентифікатор. Для нових сутностей — значення за замовчуванням.</summary>
    public TId Id { get; protected set; } = default!;

    /// <summary>Чи збережена сутність у сховищі.</summary>
    public bool IsPersisted => !EqualityComparer<TId>.Default.Equals(Id, default!);

    public override bool Equals(object? obj)
        => obj is Entity<TId> other
           && other.GetType() == GetType()
           && IsPersisted
           && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override int GetHashCode() => HashCode.Combine(GetType(), Id);
}
