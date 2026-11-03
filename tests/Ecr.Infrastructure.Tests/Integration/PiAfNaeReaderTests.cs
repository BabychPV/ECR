// tests/Ecr.Infrastructure.Tests/Integration/PiAfNaeReaderTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Integration;

/// <summary>
/// NAE node types (human 03.11): the live AF load maps by TEMPLATE, so the reader passes the element template from the adapter
/// (<see cref="SourceElement.TemplateName"/>) into <c>NaeAfElement.Template</c>, and over PI Web API — which lists only direct
/// children — walks the whole typed subtree, without descending into NAE sources (template <c>Equipment</c>).
/// </summary>
/// <remarks>
/// Mutation proofs (local, not committed): pass <c>null</c> instead of <c>e.TemplateName</c> → the template assertion fails;
/// read the root only (no walk) → the depth assertion fails; drop the Equipment leaf rule → the "no request below a source"
/// assertion fails.
/// </remarks>
[Collection("SqlServer")]
public sealed class PiAfNaeReaderTests(SqlServerFixture sql)
{
    private const string Root = @"\\AF\ECR\ECR_01_Air";

    private static readonly string[] Names = ["Atyrau", "Offshore", "Island_A", "Block_2", "1086", "1085"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Web_api__walks_the_typed_subtree_and_passes_the_template_through()
    {
        var adapter = Substitute.For<IExternalDataSource>();
        adapter.Transport.Returns(ExternalTransport.PiWebApi);
        var children = new Dictionary<string, SourceElement[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Root] = [El(@"Atyrau", "Air_Region")],
            [$@"{Root}\Atyrau"] = [El(@"Atyrau\Offshore", "Air_OnShoreOffShore")],
            [$@"{Root}\Atyrau\Offshore"] = [El(@"Atyrau\Offshore\Island_A", "Air_Area")],
            [$@"{Root}\Atyrau\Offshore\Island_A"] = [El(@"Atyrau\Offshore\Island_A\Block_2", "Air_Area"), El(@"Atyrau\Offshore\Island_A\1086", "Equipment")],
            [$@"{Root}\Atyrau\Offshore\Island_A\Block_2"] = [El(@"Atyrau\Offshore\Island_A\Block_2\1085", "Equipment")],
        };
        adapter.DiscoverElementsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new SourceElementsResult(children.GetValueOrDefault(call.ArgAt<string>(1)) ?? [], true));
        adapter.ReadCurrentAsync(Arg.Any<int>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new CurrentValuesResult([], []));

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        int dataSourceId;
        await using (var db = builder.CreateContext())
        {
            var source = new DataSource(
                EcrCode.Create($"NAER{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "AF" }),
                ExternalTransport.PiWebApi, "https://pi.example/piwebapi", "secret");
            db.DataSources.Add(source);
            await db.SaveChangesAsync();
            dataSourceId = source.Id;
        }

        await using var context = builder.CreateContext();
        var snapshot = await new PiAfNaeReader(context, [adapter]).ReadAsync(dataSourceId, Root, CancellationToken.None);

        Assert.True(snapshot.IsComplete);
        var byName = snapshot.Elements.ToDictionary(e => e.Name);
        Assert.Equal(
            Names.Order(StringComparer.Ordinal),
            byName.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("Air_Area", "Equipment"), (byName["Block_2"].Template, byName["1085"].Template));

        // No request for the children of an NAE source.
        await adapter.DidNotReceive().DiscoverElementsAsync(Arg.Any<int>(), $@"{Root}\Atyrau\Offshore\Island_A\1086", Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Walk__stops_at_the_depth_cap_and_marks_the_listing_incomplete()
    {
        var adapter = Substitute.For<IExternalDataSource>();
        adapter.DiscoverElementsAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new SourceElementsResult([new SourceElement(Guid.NewGuid().ToString(), "G", call.ArgAt<string>(1) + @"\G", "x", "Air_Area")], true));

        var result = await PiAfNaeReader.WalkAsync(adapter, 1, Root, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(PiAfNaeReader.MaxDepth, result.Elements.Count);
    }

    private static SourceElement El(string relative, string template)
        => new(Guid.NewGuid().ToString(), relative.Split('\\')[^1], $@"{Root}\{relative}", $@"{Root}\{relative}", template);
}
