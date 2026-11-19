// tests/Ecr.Calculations.Tests/MethodologyResolverRowDefaultTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// ✎ B175 × D-425: НОВИЙ документ (жодної комірки статусу) — рядки з дефолтом рядка <c>Status = RUNNING</c>
/// проходять фільтр активності, рядок без нього — виключений. Шлях справжній: <see cref="ColumnDefaultValues"/>
/// доповнює зріз ДО фільтра в <see cref="MethodologyResolver.MatchRowsDetailedAsync"/>.
/// </summary>
/// <remarks>
/// Мутація: прибрати доповнення дефолтами рядків (<c>OfRow</c> → <c>Of</c> у <c>ColumnDefaultSet.Complete</c>) —
/// R000 і R001 теж виключені, тест червоний.
/// </remarks>
public sealed class MethodologyResolverRowDefaultTests
{
    private const long InstanceId = 500;
    private const int Version = 3;
    private const int Period = 202601;
    private const int Registry = 900;
    private const long Running = 77;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "B175")]
    public async Task Новий_документ_дефолт_рядка_Running_допускає_рядок_а_рядок_без_дефолту_виключений()
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var table = builder.Table(builder.Sheet("Land"), "T7b");
        var status = builder.Column(table, "Land_Status", CellDataType.Lookup);
        status.SetLookup(Registry);
        builder.Column(table, "Fuel", CellDataType.String);
        builder.Row(table, "R000").SetCellDefaults(new Dictionary<string, string> { ["Land_Status"] = "RUNNING" });
        builder.Row(table, "R001").SetCellDefaults(new Dictionary<string, string> { ["Land_Status"] = "RUNNING" });
        builder.Row(table, "R002");

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());
        var registries = Substitute.For<IRegistryStore>();
        registries.FindEntriesByCodesAsync(Registry, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RegistryEntry>)
            [
                RegistryEntry.ForSnapshot(
                    Running, Registry, EcrCode.Create("RUNNING"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Running" }), 1),
            ]);

        var store = Substitute.For<IMethodologyStore>();
        store.GetRulesAsync(71, Arg.Any<CancellationToken>())
            .Returns([new MethodologyRule(71, EcrCode.Create("RULE"), "{}", priority: 10)]);

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(InstanceId, 700, table.Id, Version, Period));
        rows.GetRowIdsAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R000"] = 1000, ["R001"] = 1001, ["R002"] = 1002 });

        // Новий документ: жодної збереженої комірки.
        var cells = Substitute.For<ICellStore>();
        cells.ReadSliceAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([]);

        var activity = Substitute.For<IRowActivityFilterSource>();
        activity.ResolveForVersionAsync(71, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new RowActivityFilterResolution(
                Running.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<int, IReadOnlyList<int>> { [table.Id] = [status.Id] }));

        var resolver = new MethodologyResolver(
            store, cells, rows, columnMapper: null, rowActivity: activity,
            defaults: new ColumnDefaultValues(metadata, registries));

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000", "R001"], outcome.Matches.Select(m => m.RowKey));
        Assert.Equal(["R002"], outcome.Excluded);
    }
}
