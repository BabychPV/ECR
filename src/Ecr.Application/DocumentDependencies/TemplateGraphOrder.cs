// src/Ecr.Application/DocumentDependencies/TemplateGraphOrder.cs
namespace Ecr.Application.DocumentDependencies;

/// <summary>
/// B179 L1: статичний порядок графа шаблонів — сильно зв'язні компоненти (Тарʼян) і рівні Кана над їх конденсацією.
/// </summary>
/// <remarks>
/// ⚠ Цикл тут — лише зауваження (дизайн §4): відмову публікації чи перемикання зв'язки дає L3, а рантайм тримає стеля
/// хопів. Компонента з циклом отримує ОДИН рівень (усі її шаблони), і шаблони нижче неї теж отримують рівні — інакше
/// один цикл вимкнув би порядок усього графа.
/// </remarks>
public static class TemplateGraphOrder
{
    /// <summary>Рівні й цикли.</summary>
    /// <param name="nodes">Шаблони.</param>
    /// <param name="edges">Ребра «джерело → споживач».</param>
    /// <returns>Рівні (кожен — відсортовані Id) і цикли (компоненти з &gt; 1 вузлом або петлею).</returns>
    public static (IReadOnlyList<IReadOnlyList<int>> Levels, IReadOnlyList<IReadOnlyList<int>> Cycles) Compute(
        IEnumerable<int> nodes, IEnumerable<(int From, int To)> edges)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var edgeList = edges.Distinct().ToList();
        var all = new SortedSet<int>(nodes);
        foreach (var (from, to) in edgeList)
        {
            all.Add(from);
            all.Add(to);
        }

        var next = all.ToDictionary(n => n, _ => new List<int>());
        foreach (var (from, to) in edgeList)
        {
            next[from].Add(to);
        }

        var components = StronglyConnected(all, next);
        var componentOf = new Dictionary<int, int>();
        for (var c = 0; c < components.Count; c++)
        {
            foreach (var node in components[c])
            {
                componentOf[node] = c;
            }
        }

        var cycles = components
            .Where(c => c.Count > 1 || edgeList.Contains((c[0], c[0])))
            .Select(c => (IReadOnlyList<int>)c)
            .ToList();

        // Кан над конденсацією: ребро між різними компонентами.
        var indegree = new int[components.Count];
        var outgoing = Enumerable.Range(0, components.Count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (from, to) in edgeList)
        {
            var a = componentOf[from];
            var b = componentOf[to];
            if (a != b && outgoing[a].Add(b))
            {
                indegree[b]++;
            }
        }

        var levels = new List<IReadOnlyList<int>>();
        var frontier = Enumerable.Range(0, components.Count).Where(c => indegree[c] == 0).ToList();
        while (frontier.Count > 0)
        {
            levels.Add([.. frontier.SelectMany(c => components[c]).Order()]);
            var following = new List<int>();
            foreach (var c in frontier)
            {
                foreach (var d in outgoing[c])
                {
                    if (--indegree[d] == 0)
                    {
                        following.Add(d);
                    }
                }
            }

            frontier = following;
        }

        return (levels, cycles);
    }

    /// <summary>Тарʼян, ітеративно (без рекурсії на довгих ланцюгах); кожна компонента — відсортовані Id.</summary>
    private static List<List<int>> StronglyConnected(SortedSet<int> nodes, Dictionary<int, List<int>> next)
    {
        var index = new Dictionary<int, int>();
        var low = new Dictionary<int, int>();
        var onStack = new HashSet<int>();
        var stack = new Stack<int>();
        var result = new List<List<int>>();
        var counter = 0;

        foreach (var start in nodes)
        {
            if (index.ContainsKey(start))
            {
                continue;
            }

            var work = new Stack<(int Node, int Edge)>();
            work.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack.Add(start);

            while (work.TryPop(out var frame))
            {
                var (node, edge) = frame;
                if (edge < next[node].Count)
                {
                    work.Push((node, edge + 1));
                    var to = next[node][edge];
                    if (!index.TryGetValue(to, out var seen))
                    {
                        index[to] = low[to] = counter++;
                        stack.Push(to);
                        onStack.Add(to);
                        work.Push((to, 0));
                    }
                    else if (onStack.Contains(to))
                    {
                        low[node] = Math.Min(low[node], seen);
                    }

                    continue;
                }

                if (work.TryPeek(out var parent))
                {
                    low[parent.Node] = Math.Min(low[parent.Node], low[node]);
                }

                if (low[node] == index[node])
                {
                    var component = new List<int>();
                    int member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    }
                    while (member != node);

                    component.Sort();
                    result.Add(component);
                }
            }
        }

        return result;
    }
}
