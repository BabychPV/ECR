namespace Ecr.Expressions.Graph;

/// <summary>
/// Топологічне сортування. Цикл повертається як **помилка публікації**
/// (<c>ECR-TMPL-4221</c>), а не як тихо неправильне число в проді (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorter
{
    /// <summary>Сортує вузли.</summary>
    /// <returns>Порядок обчислення або перелік вузлів, що утворюють цикл.</returns>
    public OrderingResult Sort(DependencyGraph graph)
        => throw new NotImplementedException(
            "TODO: алгоритм Кана. При виявленні циклу — ПОВЕРНУТИ шлях циклу, а не просто " +
            "прапорець: користувач має побачити, які саме формули замкнулися.");
}

/// <summary>Результат сортування.</summary>
/// <param name="IsSuccess">Чи вдалося впорядкувати.</param>
/// <param name="Order">Вузли в порядку обчислення.</param>
/// <param name="CyclePath">Шлях циклу, якщо він є.</param>
public sealed record OrderingResult(bool IsSuccess, IReadOnlyList<int> Order, IReadOnlyList<int>? CyclePath);
