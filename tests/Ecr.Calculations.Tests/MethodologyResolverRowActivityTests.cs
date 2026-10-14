// tests/Ecr.Calculations.Tests/MethodologyResolverRowActivityTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// D-425: фільтр активності рядків («лише Running» для Land) у <see cref="MethodologyResolver"/>.
/// </summary>
/// <remarks>
/// Мутації (кожна червонить названий тест): вирізати відсів у <c>MatchRowsDetailedAsync</c> — перший; інвертувати
/// порівняння — перший і шостий; NULL = Running — другий; ігнорувати прапорець (фільтр без перевірки) — третій;
/// прибрати перевірку «усі колонки» — шостий.
/// </remarks>
public sealed class MethodologyResolverRowActivityTests
{
    private const long InstanceId = 500;
    private const int TableDefId = 3;
    private const int Period = 202601;
    private const int StatusColumn = 12;
    private const int SecondStatusColumn = 13;
    private const int FuelColumn = 11;
    private const long Running = 77;
    private const long Removed = 78;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Прапорець_увімкнено_рахується_лише_Running_а_Removed_і_порожній_статус_виключені()
    {
        var resolver = Arrange(
            rule: "{}",
            cells: [Status(1000, Running), Status(1001, Removed), Fuel(1002, "Gas")],
            activity: Active(StatusColumn));

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000"], outcome.Matches.Select(m => m.RowKey));
        Assert.Equal(["R000", "R001", "R002"], outcome.AllRowKeys);
        Assert.Equal(["R001", "R002"], outcome.Excluded);
        Assert.False(outcome.FilterInert);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Рядок_без_комірки_статусу_NULL_не_рахується()
    {
        var resolver = Arrange(
            rule: "{}",
            cells: [Status(1000, Running), Fuel(1001, "Gas"), Fuel(1002, "Coal")],
            activity: Active(StatusColumn));

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000"], outcome.Matches.Select(m => m.RowKey));
        Assert.Equal(["R001", "R002"], outcome.Excluded);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Без_прапорця_рахуються_усі_рядки_opt_in()
    {
        var resolver = Arrange(
            rule: "{}",
            cells: [Status(1000, Running), Status(1001, Removed)],
            activity: null);

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000", "R001", "R002"], outcome.Matches.Select(m => m.RowKey));
        Assert.Empty(outcome.Excluded);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Правило_з_умовою_Status_Running_дає_ті_самі_рядки_з_прапорцем_і_без_нього()
    {
        var cells = new[] { Status(1000, Running), Status(1001, Removed), Status(1002, Running) };
        var rule = $$"""{"{{StatusColumn}}":"{{Running}}"}""";

        var without = await Arrange(rule, cells, activity: null)
            .MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);
        var with = await Arrange(rule, cells, Active(StatusColumn))
            .MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000", "R002"], without.Matches.Select(m => m.RowKey));
        Assert.Equal(without.Matches, with.Matches);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Таблиця_без_колонки_статусу_фільтр_не_діє_і_це_видно()
    {
        var resolver = Arrange(
            rule: "{}",
            cells: [Fuel(1000, "Gas"), Fuel(1001, "Coal")],
            activity: new RowActivityFilterResolution(
                Running.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new Dictionary<int, IReadOnlyList<int>>()));

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(3, outcome.Matches.Count);
        Assert.Empty(outcome.Excluded);
        Assert.True(outcome.FilterInert);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "D-425")]
    public async Task Кілька_колонок_статусу_усі_мусять_бути_Running()
    {
        var resolver = Arrange(
            rule: "{}",
            cells:
            [
                Status(1000, Running), Status(1000, Running, SecondStatusColumn),
                Status(1001, Running), Status(1001, Removed, SecondStatusColumn),
                Status(1002, Running),
            ],
            activity: Active(StatusColumn, SecondStatusColumn));

        var outcome = await resolver.MatchRowsDetailedAsync(71, InstanceId, CancellationToken.None);

        Assert.Equal(["R000"], outcome.Matches.Select(m => m.RowKey));
        Assert.Equal(["R001", "R002"], outcome.Excluded);
    }

    private static RowActivityFilterResolution Active(params int[] statusColumns)
        => new(
            Running.ToString(System.Globalization.CultureInfo.InvariantCulture),
            new Dictionary<int, IReadOnlyList<int>> { [TableDefId] = statusColumns });

    private static MethodologyResolver Arrange(
        string rule, CellRecord[] cells, RowActivityFilterResolution? activity)
    {
        var store = Substitute.For<IMethodologyStore>();
        store.GetRulesAsync(71, Arg.Any<CancellationToken>())
            .Returns([new MethodologyRule(71, Domain.ValueObjects.EcrCode.Create("RULE"), rule, priority: 10)]);

        var rows = Substitute.For<IRowStore>();
        rows.ResolveTableInstanceAsync(InstanceId, Arg.Any<CancellationToken>())
            .Returns(new TableInstanceRef(InstanceId, 700, TableDefId, 3, Period));
        rows.GetRowIdsAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, long> { ["R000"] = 1000, ["R001"] = 1001, ["R002"] = 1002 });

        var cellStore = Substitute.For<ICellStore>();
        cellStore.ReadSliceAsync(InstanceId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(cells);

        var source = Substitute.For<IRowActivityFilterSource>();
        source.ResolveForVersionAsync(71, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(activity);

        return new MethodologyResolver(store, cellStore, rows, columnMapper: null, rowActivity: source);
    }

    private static CellRecord Status(long rowId, long entryId, int column = StatusColumn)
        => new(
            new CellAddress(new PeriodKey(Period), rowId, column),
            TableDefId,
            new CellValueData { ValueRegistryEntryId = entryId });

    private static CellRecord Fuel(long rowId, string value)
        => new(
            new CellAddress(new PeriodKey(Period), rowId, FuelColumn),
            TableDefId,
            new CellValueData { ValueString = value });
}
