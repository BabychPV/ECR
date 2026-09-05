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
}
