using Ecr.Expressions.Graph;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Graph;

/// <summary>
/// Цикл — помилка **публікації**, а не тихо неправильне число в проді
/// (ФВ-9.4).
/// </summary>
public sealed class TopologicalSorterTests
{
    private static readonly TopologicalSorter Sorter = new();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Залежності_обчислюються_раніше_за_залежні_формули()
    {
        // 3 залежить від 2, 2 — від 1. Порядок обчислення: 1, 2, 3.
        var graph = new DependencyGraph();
        graph.AddEdge(3, 2);
        graph.AddEdge(2, 1);

        var result = Sorter.Sort(graph);

        Assert.True(result.IsSuccess);
        Assert.Null(result.CyclePath);

        // ⚠ Вузол 1 не має власних ребер і не потрапляє в graph.Nodes.
        // Якби сортувальник брав лише їх, він загубив би початок ланцюга.
        Assert.Equal([1, 2, 3], result.Order);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Цикл_із_двох_формул_виявляється()
    {
        var graph = new DependencyGraph();
        graph.AddEdge(1, 2);
        graph.AddEdge(2, 1);

        var result = Sorter.Sort(graph);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.CyclePath);
        Assert.Contains(1, result.CyclePath!);
        Assert.Contains(2, result.CyclePath!);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_із_трьох_формул_виявляється()
    {
        var graph = new DependencyGraph();
        graph.AddEdge(1, 2);
        graph.AddEdge(2, 3);
        graph.AddEdge(3, 1);

        var result = Sorter.Sort(graph);

        Assert.False(result.IsSuccess);
        Assert.Empty(result.Order);
        Assert.All([1, 2, 3], node => Assert.Contains(node, result.CyclePath!));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.14")]
    public void Результат_містить_шлях_циклу_а_не_лише_прапорець()
    {
        // Ланцюг 5 → 4 → 3 не в циклі; цикл — 1 → 2 → 1.
        var graph = new DependencyGraph();
        graph.AddEdge(5, 4);
        graph.AddEdge(4, 3);
        graph.AddEdge(1, 2);
        graph.AddEdge(2, 1);

        var result = Sorter.Sort(graph);

        // Прапорця «є цикл» замало: у графі на тисячі формул пошук винуватця
        // без шляху — ручна робота, і саме тому цикл лишався б непоміченим.
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.CyclePath);
        Assert.DoesNotContain(3, result.CyclePath!);
        Assert.DoesNotContain(4, result.CyclePath!);
        Assert.DoesNotContain(5, result.CyclePath!);

        // Шлях замкнутий: перший вузол повторюється в кінці.
        Assert.Equal(result.CyclePath![0], result.CyclePath[^1]);

        // Те, що не в циклі, все одно впорядковане — публікація має показати
        // проблему точково, а не оголосити зламаним увесь шаблон.
        Assert.Equal([3, 4, 5], result.Order);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Самопосилання_формули_це_цикл()
    {
        var graph = new DependencyGraph();
        graph.AddEdge(7, 7);

        var result = Sorter.Sort(graph);

        Assert.False(result.IsSuccess);
        Assert.Equal([7, 7], result.CyclePath);
    }

    /// <summary>
    /// P-4 (аудит 2026-11-10): сортувальник без повного проходу по <c>remaining</c> на кожен вузол дає ТОЙ САМИЙ
    /// результат, що й старий алгоритм. <c>EvaluationOrder</c> фіксується при публікації й зберігається, тож порядок
    /// виходу — частина контракту.
    /// </summary>
    /// <remarks>Мутація: прибрати <c>released.Sort(...)</c> у <see cref="TopologicalSorter"/> — порядок розходиться.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public void Порядок_збігається_з_еталонним_алгоритмом()
    {
        for (var seed = 1; seed <= 50; seed++)
        {
            var random = new Random(seed);
            var size = random.Next(1, 301);
            var acyclic = seed % 3 != 0;
            var graph = new DependencyGraph();

            // Вузли додаються в «перемішаному» порядку: порядок вставки визначає порядок звільнення залежних.
            foreach (var node in Enumerable.Range(1, size).OrderBy(_ => random.Next()))
            {
                var edges = random.Next(0, 4);
                for (var e = 0; e < edges; e++)
                {
                    var dependency = acyclic
                        ? (node > 1 ? random.Next(1, node) : 0)
                        : random.Next(1, size + 3); // +3: вузли, що є лише залежністю
                    if (dependency > 0)
                    {
                        graph.AddEdge(node, dependency);
                    }
                }

                if (!acyclic && random.Next(0, 40) == 0)
                {
                    graph.AddEdge(node, node);
                }
            }

            var expected = ReferenceSorter.Sort(graph);
            var actual = Sorter.Sort(graph);

            Assert.True(expected.IsSuccess == actual.IsSuccess, $"seed {seed}: IsSuccess");
            Assert.True(expected.Order.SequenceEqual(actual.Order), $"seed {seed}: Order розійшовся");
            Assert.True(
                (expected.CyclePath ?? []).SequenceEqual(actual.CyclePath ?? []),
                $"seed {seed}: CyclePath розійшовся");
        }
    }

    /// <summary>
    /// P-4: Кан із повним проходом по <c>remaining</c> на кожен вузол — O(V²): на ланцюгу з 40 000 формул ~800 млн
    /// ітерацій під блоком версії при публікації. Бюджет щедрий (лінійний шлях — десятки мілісекунд).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сортування_ланцюга_на_40000_вузлів_не_квадратичне()
    {
        const int size = 40_000;
        var graph = new DependencyGraph();
        for (var node = 2; node <= size; node++)
        {
            graph.AddEdge(node, node - 1);
        }

        // Прогрів (JIT), потім найкращий із трьох замірів.
        _ = Sorter.Sort(graph);
        var best = TimeSpan.MaxValue;
        OrderingResult? result = null;
        for (var i = 0; i < 3; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            result = Sorter.Sort(graph);
            watch.Stop();
            best = watch.Elapsed < best ? watch.Elapsed : best;
        }

        Assert.True(result!.IsSuccess);
        Assert.Equal(size, result.Order.Count);
        Assert.True(
            best < TimeSpan.FromMilliseconds(1000),
            $"Ланцюг на {size} вузлів сортувався {best.TotalMilliseconds:F0} мс (бюджет 1000 мс; O(V²) — секунди).");
    }

    /// <summary>Дослівна копія сортувальника ДО P-4 — еталон порядку виходу.</summary>
    private static class ReferenceSorter
    {
        public static OrderingResult Sort(DependencyGraph graph)
        {
            var nodes = new HashSet<int>(graph.Nodes);
            foreach (var node in graph.Nodes)
            {
                foreach (var dependency in graph.DependenciesOf(node))
                {
                    nodes.Add(dependency);
                }
            }

            var remaining = nodes.ToDictionary(
                node => node,
                node => new HashSet<int>(graph.DependenciesOf(node).Where(nodes.Contains)));

            var order = new List<int>(nodes.Count);
            var ready = new Queue<int>(remaining.Where(p => p.Value.Count == 0)
                                                .Select(p => p.Key)
                                                .OrderBy(id => id));

            while (ready.Count > 0)
            {
                var node = ready.Dequeue();
                order.Add(node);
                remaining.Remove(node);

                foreach (var (dependent, dependencies) in remaining)
                {
                    if (dependencies.Remove(node) && dependencies.Count == 0)
                    {
                        ready.Enqueue(dependent);
                    }
                }
            }

            return remaining.Count == 0
                ? new OrderingResult(true, order, null)
                : new OrderingResult(false, order, FindCycle(remaining));
        }

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
            int node, Dictionary<int, HashSet<int>> graph, List<int> path, HashSet<int> onPath, HashSet<int> visited)
        {
            if (onPath.Contains(node))
            {
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
}
