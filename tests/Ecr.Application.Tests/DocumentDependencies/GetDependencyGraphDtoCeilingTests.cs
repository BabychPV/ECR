// tests/Ecr.Application.Tests/DocumentDependencies/GetDependencyGraphDtoCeilingTests.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Domain.Entities.Configuration;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.DocumentDependencies;

/// <summary>
/// F13 (B179 L4): стелі відповіді <c>GET /dependency-graph</c> (правило 6) — ребер не більше <c>MaxEdges</c>, доказів на
/// ребро не більше <c>MaxEvidencePerEdge</c>; <c>truncated</c> і <c>evidenceTotal</c> чесні на межі.
/// </summary>
/// <remarks>
/// Мутації в <c>GetDependencyGraphHandler.ToDto</c>: <c>&gt; MaxEdges</c> → <c>&gt;=</c> - червоний тест на межі 5000;
/// <c>Take(MaxEdges)</c> прибрати - червоний; <c>Take(MaxEvidencePerEdge)</c> прибрати - червоний; <c>e.Evidence.Count</c> →
/// кількість показаних - червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait("Directive", "B179")]
public sealed class GetDependencyGraphDtoCeilingTests
{
    /// <summary>Одне джерело наповнює <paramref name="registries"/> довідників: рівно стільки ребер <c>fills</c>.</summary>
    private static DependencyGraphSnapshot Fills(int registries, int bindingsPerRegistry = 1)
    {
        var codes = Enumerable.Range(1, registries).ToDictionary(i => i, i => "R" + i);
        var bindings = new List<SourceBindingRow>();
        var id = 1;
        for (var r = 1; r <= registries; r++)
        {
            for (var b = 0; b < bindingsPerRegistry; b++)
            {
                bindings.Add(new SourceBindingRow(id++, 1, r, DocumentSourceState.Any));
            }
        }

        return DependencyGraphAssembler.Assemble(new DependencyGraphInputs(
            "r", new Dictionary<int, string> { [1] = "SRC" }, codes, new Dictionary<int, string>(),
            bindings, [], [], [], [], []));
    }

    [Fact]
    public void Рівно_стеля_ребер_не_урізано_а_на_одне_більше_урізано()
    {
        var exact = GetDependencyGraphHandler.ToDto(Fills(GetDependencyGraphHandler.MaxEdges));
        Assert.Equal(GetDependencyGraphHandler.MaxEdges, exact.Edges.Count);
        Assert.False(exact.Truncated);

        var over = GetDependencyGraphHandler.ToDto(Fills(GetDependencyGraphHandler.MaxEdges + 1));
        Assert.Equal(GetDependencyGraphHandler.MaxEdges, over.Edges.Count);
        Assert.True(over.Truncated);
    }

    [Fact]
    public void Докази_на_ребро_урізано_до_стелі_а_evidenceTotal_каже_скільки_всього()
    {
        var total = GetDependencyGraphHandler.MaxEvidencePerEdge + 5;
        var dto = GetDependencyGraphHandler.ToDto(Fills(registries: 1, bindingsPerRegistry: total));

        var edge = Assert.Single(dto.Edges);
        Assert.Equal(GetDependencyGraphHandler.MaxEvidencePerEdge, edge.Evidence.Count);
        Assert.Equal(total, edge.EvidenceTotal);
    }
}
