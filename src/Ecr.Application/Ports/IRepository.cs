namespace Ecr.Application.Ports;

/// <summary>
/// Сховище агрегата. Навмисно вузьке: <see cref="IQueryable{T}"/> назовні не
/// віддається, бо тоді деталі провайдера протікають у use-cases і
/// <c>ToList()</c> без <c>Take()</c> стає питанням дисципліни, а не типу.
/// </summary>
/// <typeparam name="T">Тип агрегата.</typeparam>
/// <typeparam name="TId">Тип ідентифікатора.</typeparam>
public interface IRepository<T, in TId> where T : class
{
    /// <summary>Знаходить за ідентифікатором або повертає <c>null</c>.</summary>
    public Task<T?> FindAsync(TId id, CancellationToken ct);

    /// <summary>Знаходить або кидає <see cref="Errors.NotFoundException"/>.</summary>
    public Task<T> GetAsync(TId id, CancellationToken ct);

    /// <summary>Додає новий агрегат.</summary>
    public void Add(T entity);

    /// <summary>Позначає агрегат видаленим (фізичне видалення — лише де це дозволено).</summary>
    public void Remove(T entity);
}
