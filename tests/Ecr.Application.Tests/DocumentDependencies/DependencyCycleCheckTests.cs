// tests/Ecr.Application.Tests/DocumentDependencies/DependencyCycleCheckTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Binding;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.DocumentDependencies;

/// <summary>
/// B179 L3: «що, якби» публікації версії шаблону над графом — відмова лише на НОВИЙ цикл через сам шаблон, без бази.
/// </summary>
/// <remarks>
/// Мутації (локально, B179 L3): ребра нової версії не додаються → <see cref="Нова_версія_що_читає_власний_довідник_замикає_петлю"/>
/// і <see cref="Цикл_через_інший_шаблон_і_правило_довідника_дає_повний_шлях"/> червоні; будь-який наявний цикл блокує →
/// <see cref="Чужий_наявний_цикл_не_блокує"/> червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class DependencyCycleCheckTests
{
    // Шаблони: 10 HSE400 (наповнює 100), 20 LAND (наповнює 200), 30/40 — чужий цикл через 300.
    private static DependencyGraphSnapshot Graph(params TemplateRegistryRead[] reads)
        => DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r",
            new Dictionary<int, string> { [10] = "HSE400", [20] = "LAND_UI", [30] = "X", [40] = "Y" },
            new Dictionary<int, string> { [100] = "COMPOSITION", [101] = "DERIVED", [200] = "LAND_OUT", [300] = "XR" },
            new Dictionary<int, string>(),
            [
                new SourceBindingRow(1, 10, 100, DocumentSourceState.Any),
                new SourceBindingRow(2, 20, 200, DocumentSourceState.Any),
                new SourceBindingRow(3, 30, 300, DocumentSourceState.Any),
            ],
            [
                new TemplateRegistryRead(20, 2000, 100, PeriodScope.Month, 1, "formulaDependency", "t"),
                new TemplateRegistryRead(30, 3000, 300, PeriodScope.All, 2, "formulaDependency", "t"),
                .. reads,
            ],
            [],
            [],
            [new RegistryRuleRead(55, 101, 200)],
            []));

    private static string Path(DependencyGraphSnapshot graph, IReadOnlyList<DependencyNodeRef>? cycle)
        => cycle is null ? "—" : DependencyCycleCheck.Describe(graph, cycle);

    [Fact]
    public void Нова_версія_що_читає_власний_довідник_замикає_петлю()
    {
        var graph = Graph();

        Assert.Equal("HSE400 → COMPOSITION → HSE400", Path(graph, DependencyCycleCheck.ForTemplatePublish(graph, 10, [100])));
    }

    [Fact]
    public void Цикл_через_інший_шаблон_і_правило_довідника_дає_повний_шлях()
    {
        // HSE400 → COMPOSITION → LAND_UI → LAND_OUT → (правило) DERIVED → HSE400.
        var graph = Graph();

        Assert.Equal(
            "HSE400 → COMPOSITION → LAND_UI → LAND_OUT → DERIVED → HSE400",
            Path(graph, DependencyCycleCheck.ForTemplatePublish(graph, 10, [101])));
    }

    [Fact]
    public void Версія_без_циклу_і_шаблон_що_вже_лежав_на_циклі_проходять()
    {
        var graph = Graph();
        Assert.Null(DependencyCycleCheck.ForTemplatePublish(graph, 10, [300]));

        // Цикл HSE400 → COMPOSITION → LAND_UI → LAND_OUT → DERIVED → HSE400 уже є: нова версія LAND_UI його не створює
        // (виправлення чи косметична публікація такого шаблону не блокуються; цикл — зауваження графа).
        var withCycle = Graph(new TemplateRegistryRead(10, 1000, 101, PeriodScope.All, 3, "formulaDependency", "t"));
        Assert.Contains(withCycle.Cycles, c => c.Contains(20));
        Assert.Null(DependencyCycleCheck.ForTemplatePublish(withCycle, 20, [100]));
    }

    [Fact]
    public void Чужий_наявний_цикл_не_блокує()
    {
        var graph = Graph();
        Assert.NotEmpty(graph.Cycles); // X → XR → X

        Assert.Null(DependencyCycleCheck.ForTemplatePublish(graph, 40, [100, 200]));
    }
}
