namespace Ecr.Expressions.Graph;

/// <summary>
/// Топологічне сортування. Цикл повертається як **помилка публікації**
/// (<c>ECR-TMPL-4221</c>), а не як тихо неправильне число в проді (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorter
{
    /// <summary>Сортує вузли.</summary>
    /// <param name="graph">Граф залежностей формул.</param>
    /// <returns>Порядок обчислення або перелік вузлів, що утворюють цикл.</returns>
    public OrderingResult Sort(DependencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // ⚠ Вузол може бути ЛИШЕ залежністю і не мати власних ребер — такий не
        // потрапляє в graph.Nodes. Пропустити його означало б порахувати
        // порядок без нього і вирішити, що решта утворює цикл.
        var nodes = new HashSet<int>(graph.Nodes);
        foreach (var node in graph.Nodes)
        {
            foreach (var dependency in graph.DependenciesOf(node))
            {
                nodes.Add(dependency);
            }
        }

        // ⛔ P-4 (аудит 2026-11-10): зворотні ребра замість повного проходу по `remaining` на кожен вузол (O(V²):
        // ~800 млн ітерацій на 40 000 формул під блоком версії при публікації). Порядок виходу ТОЙ САМИЙ — він
        // фіксується в `EvaluationOrder` і зберігається: звільнені вузли ставляться в чергу в порядку перебору `nodes`
        // (= порядок вставки колишнього словника `remaining`, у який після побудови нічого не додавалось).
        // Сторож — `TopologicalSorterTests.Порядок_збігається_з_еталонним_алгоритмом`.
        var position = new Dictionary<int, int>(nodes.Count);
        var remaining = new Dictionary<int, HashSet<int>>(nodes.Count);
        var dependents = new Dictionary<int, List<int>>();
        var next = 0;
        foreach (var node in nodes)
        {
            position[node] = next++;
            var dependencies = new HashSet<int>(graph.DependenciesOf(node).Where(nodes.Contains));
            remaining[node] = dependencies;
            foreach (var dependency in dependencies)
            {
                if (!dependents.TryGetValue(dependency, out var list))
                {
                    dependents[dependency] = list = [];
                }

                list.Add(node);
            }
        }

        var order = new List<int>(nodes.Count);
        var ready = new Queue<int>(remaining.Where(p => p.Value.Count == 0)
                                            .Select(p => p.Key)
                                            .OrderBy(id => id));
        var released = new List<int>();

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            order.Add(node);
            remaining.Remove(node);

            if (!dependents.TryGetValue(node, out var waiting))
            {
                continue;
            }

            released.Clear();
            foreach (var dependent in waiting)
            {
                if (remaining.TryGetValue(dependent, out var dependencies)
                    && dependencies.Remove(node) && dependencies.Count == 0)
                {
                    released.Add(dependent);
                }
            }

            released.Sort((a, b) => position[a].CompareTo(position[b]));
            foreach (var dependent in released)
            {
                ready.Enqueue(dependent);
            }
        }

        if (remaining.Count == 0)
        {
            return new OrderingResult(true, order, null);
        }

        // Прапорця «є цикл» замало: користувач має побачити, ЯКІ САМЕ формули
        // замкнулися. Без шляху пошук винуватця в графі на тисячі вузлів —
        // ручна робота, і саме тому цикл лишався б непоміченим.
        return new OrderingResult(false, order, FindCycle(remaining));
    }

    /// <summary>Знаходить конкретний цикл серед вузлів, які не впорядкувалися.</summary>
    private static List<int> FindCycle(Dictionary<int, HashSet<int>> remaining)
    {
        var path = new List<int>();
        var onPath = new HashSet<int>();
        var visited = new HashSet<int>();

        foreach (var start in remaining.Keys.OrderBy(id => id))
        {
            if (Walk(start, remaining, path, onPath, visited))
            {
                return path;
            }
        }

        return remaining.Keys.OrderBy(id => id).ToList();
    }

    private static bool Walk(
        int node,
        Dictionary<int, HashSet<int>> graph,
        List<int> path,
        HashSet<int> onPath,
        HashSet<int> visited)
    {
        if (onPath.Contains(node))
        {
            // Замкнули коло: лишаємо шлях від першого входження і повторюємо
            // вузол у кінці, щоб замкнутість була видна з самого списку.
            var from = path.IndexOf(node);
            path.RemoveRange(0, from);
            path.Add(node);
            return true;
        }

        if (!visited.Add(node) || !graph.TryGetValue(node, out var dependencies))
        {
            return false;
        }

        path.Add(node);
        onPath.Add(node);

        foreach (var dependency in dependencies.OrderBy(id => id))
        {
            if (Walk(dependency, graph, path, onPath, visited))
            {
                return true;
            }
        }

        onPath.Remove(node);
        path.RemoveAt(path.Count - 1);
        return false;
    }
}

/// <summary>Результат сортування.</summary>
/// <param name="IsSuccess">Чи вдалося впорядкувати.</param>
/// <param name="Order">Вузли в порядку обчислення.</param>
/// <param name="CyclePath">Шлях циклу, якщо він є.</param>
public sealed record OrderingResult(bool IsSuccess, IReadOnlyList<int> Order, IReadOnlyList<int>? CyclePath);
