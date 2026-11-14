// tests/Ecr.Application.Tests/Calculations/CalculatedCellOverlayOwnerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// ✎ LAND-YEAR L2b: колонка <c>Calculated</c> з чужим власником (<see cref="OverlayTable.CalculatedOwnerPeriods"/>) бере
/// число актуального прогону ПЕРІОДУ-ВЛАСНИКА і його екземпляра цієї таблиці (<see cref="ICalculationYearReader"/>), а не
/// прогону перегляду; число іншої таблиці того самого періоду з тим самим ключем рядка не береться (D-401).
/// </summary>
public sealed class CalculatedCellOverlayOwnerTests
{
    private const long DocumentId = 29;
    private const int View = 209901;
    private const int Sep = 209909;
    private const int Oct = 209910;
    private const long ViewInstance = 600;
    private const long SepInstance = 609;
    private const long OctInstance = 610;
    private const long OtherTableOctInstance = 710;
    private const int TableDefId = 293;
    private const int OtherTableDefId = 294;
    private const int M01 = 7001;
    private const int M09 = 7009;
    private const int M10 = 7010;

    private static readonly IReadOnlyDictionary<string, long> Rows =
        new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1 };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-474")]
    public async Task Місячні_колонки_беруть_числа_прогонів_своїх_місяців_а_власна_колонка_прогону_перегляду()
    {
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ColumnResultBinding(TableDefId, M01, 2, "GAS_T", "{}", [5]),
                new ColumnResultBinding(TableDefId, M09, 2, "GAS_T", "{}", [5]),
                new ColumnResultBinding(TableDefId, M10, 2, "GAS_T", "{}", [5]),
            ]);

        var results = Substitute.For<ICalculationResultStore, ICalculationYearReader>();
        results.ReadCurrentAsync(DocumentId, View, Arg.Any<CancellationToken>())
            .Returns([new CalculationResultRow(5, "R1", "GAS_T", 1m, 8, null, ViewInstance)]);

        var year = (ICalculationYearReader)results;
        year.ReadTableInstancesAsync(DocumentId, Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new TableInstanceRef(SepInstance, DocumentId, TableDefId, 1, Sep),
                new TableInstanceRef(OctInstance, DocumentId, TableDefId, 1, Oct),
                new TableInstanceRef(OtherTableOctInstance, DocumentId, OtherTableDefId, 1, Oct),
            ]);
        CalculationYearReadRequest? asked = null;
        year.ReadYearAsync(Arg.Do<CalculationYearReadRequest>(r => asked = r), Arg.Any<CancellationToken>())
            .Returns(new CalculationYearFacts(
                [],
                [],
                [
                    new YearResult(Sep, 5, SepInstance, "R1", "GAS_T", null, 8, 9m),
                    new YearResult(Oct, 5, OctInstance, "R1", "GAS_T", null, 8, 10m),

                    // Інша таблиця, той самий ключ рядка й вихід, той самий місяць — не її число.
                    new YearResult(Oct, 5, OtherTableOctInstance, "R1", "GAS_T", null, 8, 77m),
                ],
                [], [], [], [], []));

        var output = await new CalculatedCellOverlay(methodologies, results).ApplyAsync(
            DocumentId,
            View,
            [
                new OverlayTable(
                    ViewInstance, TableDefId, Rows, [], new HashSet<int> { M01, M09, M10 },
                    new Dictionary<int, int> { [M09] = Sep, [M10] = Oct }),
            ],
            CancellationToken.None);

        var cells = output[ViewInstance];
        Assert.Equal(1m, Value(cells, M01));
        Assert.Equal(9m, Value(cells, M09));
        Assert.Equal(10m, Value(cells, M10));
        Assert.All(cells, c => Assert.Equal(new PeriodKey(View), c.Address.PeriodKey));

        // Один набір запитів: періоди-власники й лише екземпляри-власники цієї таблиці.
        Assert.NotNull(asked);
        Assert.Equal([Sep, Oct], asked!.PeriodKeys.Order());
        Assert.Equal([SepInstance, OctInstance], asked.TableInstanceIds.Order());
        Assert.True(asked.SliceResults);
        Assert.False(asked.Aggregate);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Decision", "D-474")]
    public async Task Без_чужих_власників_читач_року_не_викликається()
    {
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableDefId, M01, 2, "GAS_T", "{}", [5])]);
        var results = Substitute.For<ICalculationResultStore, ICalculationYearReader>();
        results.ReadCurrentAsync(DocumentId, View, Arg.Any<CancellationToken>())
            .Returns([new CalculationResultRow(5, "R1", "GAS_T", 1m, 8, null, ViewInstance)]);

        var output = await new CalculatedCellOverlay(methodologies, results).ApplyAsync(
            DocumentId, View, [new OverlayTable(ViewInstance, TableDefId, Rows, [], new HashSet<int> { M01 })], CancellationToken.None);

        Assert.Equal(1m, Value(output[ViewInstance], M01));
        await ((ICalculationYearReader)results).DidNotReceiveWithAnyArgs().ReadTableInstancesAsync(default, default!, default);
        await ((ICalculationYearReader)results).DidNotReceiveWithAnyArgs().ReadYearAsync(default!, default);
    }

    private static decimal? Value(IReadOnlyList<CellRecord> cells, int column)
        => cells.SingleOrDefault(c => c.Address.ColumnDefId == column)?.Value.ValueNumeric;
}
