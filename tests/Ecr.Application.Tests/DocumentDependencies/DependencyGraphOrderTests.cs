// tests/Ecr.Application.Tests/DocumentDependencies/DependencyGraphOrderTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.DocumentDependencies;

/// <summary>
/// B179 L1: рівні Кана над конденсацією, цикли (СЗК) і згортання до рівня шаблонів — без бази.
/// </summary>
/// <remarks>
/// Мутації (локально, B179 L1): <c>Compute</c> повертає порожні цикли → <see cref="Цикл_і_петля_звітуються_а_нижчі_шаблони_мають_рівень"/>
/// червоний; згортання без методологій → <see cref="Джерело_через_довідник_доходить_до_споживачів_формулою_і_методологією"/> червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class DependencyGraphOrderTests
{
    [Fact]
    public void Ланцюг_дає_рівні_за_порядком_без_циклів()
    {
        var (levels, cycles) = TemplateGraphOrder.Compute([1, 2, 3, 4], [(1, 2), (2, 3), (1, 3)]);

        Assert.Equal("1,4|2|3", Flat(levels));
        Assert.Empty(cycles);
    }

    [Fact]
    public void Цикл_і_петля_звітуються_а_нижчі_шаблони_мають_рівень()
    {
        var (levels, cycles) = TemplateGraphOrder.Compute([], [(1, 2), (2, 3), (3, 2), (3, 4), (5, 5)]);

        Assert.Equal("2,3|5", Flat(cycles.OrderBy(c => c[0]).ToList()));
        Assert.Equal("1,5|2,3|4", Flat(levels));
    }

    private static string Flat(IReadOnlyList<IReadOnlyList<int>> groups)
        => string.Join('|', groups.Select(g => string.Join(',', g)));

    [Fact]
    public void Джерело_через_довідник_доходить_до_споживачів_формулою_і_методологією()
    {
        // HSE400 (10) наповнює склад (100); Land (20) читає його формулою (Month) і методологією C05 (7, Year, пише в Land);
        // Report (30) читає похідний довідник (101), правило якого читає склад → All.
        var graph = DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r1",
            new Dictionary<int, string> { [10] = "HSE400", [20] = "LAND_UI", [30] = "REPORT" },
            new Dictionary<int, string> { [100] = "LAND_HSE400_COMPOSITION", [101] = "DERIVED" },
            new Dictionary<int, string> { [7] = "C05_05" },
            [new SourceBindingRow(1, 10, 100, DocumentSourceState.Any)],
            [
                new TemplateRegistryRead(20, 200, 100, PeriodScope.Month, 5000, "formulaDependency", "t"),
                new TemplateRegistryRead(30, 300, 101, PeriodScope.Month, 6000, "ast", "t"),
            ],
            [new MethodologyScopedRead(7, 70, 100, "F_DENSITY", PeriodScope.Year)],
            [new MethodologyTemplateWrite(7, 20, 900, "t")],
            [new RegistryRuleRead(55, 101, 100)],
            []));

        var land = Assert.Single(graph.ConsumersOf(10), d => d.ConsumerTemplateId == 20);
        Assert.True(land.ViaTemplateFormula && land.ViaMethodology);
        Assert.Equal(PeriodScope.Year, land.Scope);
        Assert.Equal(PeriodScope.All, Assert.Single(graph.ConsumersOf(10), d => d.ConsumerTemplateId == 30).Scope);
        Assert.Equal("10|20,30", Flat(graph.Levels));
        Assert.Empty(graph.Cycles);
        Assert.Contains(graph.Edges, e => e.Kind == DependencyEdgeKind.Fills && e.Evidence.Single().Source == "sourceBinding");
    }

    [Fact]
    public void Шаблон_що_читає_довідник_який_сам_наповнює_це_цикл()
    {
        var graph = DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r1",
            new Dictionary<int, string> { [10] = "HSE400" },
            new Dictionary<int, string> { [100] = "R" },
            new Dictionary<int, string>(),
            [new SourceBindingRow(1, 10, 100, DocumentSourceState.Any)],
            [new TemplateRegistryRead(10, 11, 100, PeriodScope.Month, 1, "ast", "t")],
            [],
            [],
            [],
            []));

        Assert.Equal(10, Assert.Single(Assert.Single(graph.Cycles)));
        Assert.Contains(graph.Diagnostics, d => d.Key == "cycle" && d.Detail.Contains("HSE400", StringComparison.Ordinal));
    }
}
