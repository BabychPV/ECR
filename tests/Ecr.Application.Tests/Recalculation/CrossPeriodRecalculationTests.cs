// tests/Ecr.Application.Tests/Recalculation/CrossPeriodRecalculationTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Recalculation;

/// <summary>
/// WW-03: рушій читає і ПЕРЕРАХОВУЄ міжперіодні посилання — абсолютні <c>[Period:#k]</c> і відносні
/// зсуви далі за −1 (G1, G2 дизайну).
/// </summary>
/// <remarks>
/// ⛔ Що було: <c>LoadValuesAsync</c> читав лише поточний період і −1 — <c>[Period:-2]</c>,
/// <c>[Period:+1]</c> мовчки давали <c>null</c>; а зворотний індекс плану ігнорував будь-який
/// ненульовий зсув — правка січня не перераховувала формулу, що читає січень з іншого періоду.
/// Вхід кожного періоду p: <c>C1 = p × 10</c> (числа різні, тож «прочитано не той період» видно).
/// </remarks>
public sealed class CrossPeriodRecalculationTests
{
    private const long DocumentId = 700;
    private const int Version = 1;
    private const int FormulaId = 555;
    private const int Year = 2026;
    private const byte CrossPeriod = 3;
    private const byte Absolute = 5;

    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();

    /// <summary>Періоди (ключі), з яких служба просила зріз комірок.</summary>
    private readonly List<int> _readPeriods = [];

    private TableDef _table = null!;
    private int _inputColumn;
    private int _outColumn;

    private static PeriodKey Key(int sequence) => PeriodKey.Create(Year, sequence);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Абсолютний_період_читає_колонку_того_ж_року_з_іншого_періоду()
    {
        // 7.0 періоду 5 читає колонку місяця 3 з періоду 3 того ж документа.
        Arrange("[Period:#3].[C1]", Absolute, 3);

        var written = await Service().RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(30m, Assert.Single(Applied()).Value.ValueNumeric);
        Assert.Contains(Key(3).Value, _readPeriods);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Абсолютний_період_що_збігається_з_власним_не_читає_сусідів()
    {
        Arrange("[Period:#5].[C1]", Absolute, 5);

        var written = await Service().RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(50m, Assert.Single(Applied()).Value.ValueNumeric);
        Assert.Equal([Key(5).Value], _readPeriods.Distinct());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    [InlineData("[C1] + [Period:-2].[C1]", -2, 80)]   // G1: −2 раніше мовчки null
    [InlineData("[C1] + [Period:+1].[C1]", 1, 110)]    // G1: +1 раніше мовчки null
    [InlineData("[C1] + [Period:-1].[C1]", -1, 90)]    // регресія: −1 як і було
    public async Task Відносні_зсуви_далі_за_мінус_один_читають_значення_а_не_null(
        string expression, int offset, int expected)
    {
        Arrange(expression, CrossPeriod, offset);

        var written = await Service().RecalculateAllAsync(DocumentId, Key(5), CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(expected, Assert.Single(Applied()).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Період_поза_роком_дає_null_а_не_помилку()
    {
        // Січень з [Period:-2] — виходу за початок року немає з чого читати (02b §3.2).
        Arrange("[C1] + [Period:-2].[C1]", CrossPeriod, -2);

        var exception = await Record.ExceptionAsync(
            () => Service().RecalculateAllAsync(DocumentId, Key(1), CancellationToken.None));

        // Номер поза межами року — не читається й не кидає: це `null`, а не збій.
        Assert.Null(exception);
        Assert.Equal([Key(1).Value], _readPeriods.Distinct());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Правка_періоду_читача_не_перераховує_інший_період_через_план_G2_закрито_fan_out_ом()
    {
        // Правка C1 у періоді 5: формула періоду 5 читає #3, не #5 — цей зріз не «брудний».
        // Перерахунок читача в ІНШОМУ періоді робить `RecalculateFromOtherPeriodsAsync`.
        Arrange("[Period:#3].[C1]", Absolute, 3);

        var written = await Service().RecalculateAsync(Instance(5), Dirty(5), CancellationToken.None);

        Assert.Equal(0, written);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Правка_власного_періоду_для_абсолютного_посилання_на_себе_йде_через_план()
    {
        Arrange("[Period:#5].[C1]", Absolute, 5);

        var written = await Service().RecalculateAsync(Instance(5), Dirty(5), CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(50m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Зміна_періоду_3_перераховує_формулу_періоду_5_що_читає_його()
    {
        Arrange("[Period:#3].[C1]", Absolute, 3);
        var changed = new HashSet<(int TableDefId, int ColumnDefId)>();

        var written = await Service().RecalculateFromOtherPeriodsAsync(
            DocumentId, Key(5), [new CrossPeriodSource(3, _table.Id, _inputColumn)], CancellationToken.None, changed);

        Assert.Equal(1, written);
        Assert.Equal(30m, Assert.Single(Applied()).Value.ValueNumeric);

        // Те, що записано в періоді-читачі, іде далі по ланцюжку читачів (його колонка Out).
        Assert.Contains((_table.Id, _outColumn), changed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Зміна_іншого_періоду_що_формула_не_читає_нічого_не_перераховує()
    {
        Arrange("[Period:#3].[C1]", Absolute, 3);

        var written = await Service().RecalculateFromOtherPeriodsAsync(
            DocumentId, Key(5), [new CrossPeriodSource(4, _table.Id, _inputColumn)], CancellationToken.None);

        Assert.Equal(0, written);
        Assert.DoesNotContain(
            _cells.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-03")]
    public async Task Відносне_ребро_мінус_один_теж_перераховується_після_зміни_попереднього_періоду()
    {
        Arrange("[C1] + [Period:-1].[C1]", CrossPeriod, -1);

        var written = await Service().RecalculateFromOtherPeriodsAsync(
            DocumentId, Key(5), [new CrossPeriodSource(4, _table.Id, _inputColumn)], CancellationToken.None);

        Assert.Equal(1, written);
        Assert.Equal(90m, Assert.Single(Applied()).Value.ValueNumeric);
    }

    private static long Instance(int sequence) => 1000 + sequence;

    private const long RowId = 2001;

    private DirtySet Dirty(int sequence)
    {
        var dirty = new DirtySet();
        dirty.Add(new CellAddress(Key(sequence), RowId, _inputColumn));

        return dirty;
    }

    private FormulaDependency Dependency(byte kind, int offset)
        => FormulaDependency.ForFormula(FormulaId, kind, _table.Id, "R1", _inputColumn, null, (short)offset, 0);

    private IReadOnlyList<CellRecord> Applied()
    {
        var call = _cells.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(ICellStore.ApplyBatchAsync));

        return [.. ((IReadOnlyCollection<CellChangeSet>)call.GetArguments()[0]!).SelectMany(set => set.Upserts)];
    }

    private RecalculationService Service()
    {
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PeriodBounds(new DateOnly(Year, 1, 1), new DateOnly(Year, 1, 31)));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        return new(
            _cells, _rows, periods, _metadata, _versions, new RealFormulaEngine(), _units,
            Substitute.For<IRegistryStore>(), headers, Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(Year, 2, 1, 0, 0, 0, DateTimeKind.Utc)),
            _uow, Substitute.For<ISheetEditGate>());
    }

    private void Arrange(string expression, byte kind, int offset)
    {
        var builder = new TemplateBuilder { TemplateVersionId = Version };
        var sheet = builder.Sheet("S");
        _table = builder.Table(sheet, "T1");
        _inputColumn = builder.Column(_table, "C1", isMonthColumn: true).Id;
        builder.Row(_table, "R1", 1);
        _outColumn = builder.Column(_table, "Out", CellDataType.Formula).Id;

        var formula = new FormulaDef(_table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, FormulaId);
        formula.AssignColumn(_outColumn);
        _table.AddFormula(formula);

        _metadata.GetAsync(Version, Arg.Any<CancellationToken>()).Returns(builder.Build());

        _rows.ResolveTableInstanceAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => new TableInstanceRef(
                call.ArgAt<long>(0), DocumentId, _table.Id, Version, Key((int)(call.ArgAt<long>(0) - 1000)).Value));
        _rows.GetTableInstancesAsync(DocumentId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var key = call.ArgAt<PeriodKey>(1);

                return (IReadOnlyList<TableInstanceRef>)
                    [new TableInstanceRef(Instance(key.Sequence), DocumentId, _table.Id, Version, key.Value)];
            });

        _rows.GetRowIdsBatchAsync(
                Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>>)
                call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                    id => id,
                    id => (IReadOnlyDictionary<string, long>)new Dictionary<string, long>(StringComparer.Ordinal)
                    {
                        ["R1"] = RowId,
                    }));

        _cells.ReadSlicesAsync(Arg.Any<IReadOnlyList<long>>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var period = call.ArgAt<PeriodKey>(1);
                _readPeriods.Add(period.Value);

                return (IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>)
                    call.ArgAt<IReadOnlyList<long>>(0).ToDictionary(
                        id => id,
                        id => (IReadOnlyList<CellRecord>)Slice(period));
            });

        // Власне ребро читання C1 цього ж періоду (формула читає колонку входу) + міжперіодне ребро.
        _versions.ListFormulaDependenciesAsync(Version, Arg.Any<CancellationToken>())
            .Returns([Dependency(kind, offset)]);
    }

    private List<CellRecord> Slice(PeriodKey period)
        =>
        [
            new(new CellAddress(period, RowId, _inputColumn), _table.Id,
                new CellValueData { ValueNumeric = period.Sequence * 10 }),

            // Застарілий підсумок: `DAT-02` не пише незміненого, тож без розбіжності не було б запису.
            new(new CellAddress(period, RowId, _outColumn), _table.Id,
                new CellValueData { ValueNumeric = -1m, IsCalculated = true }),
        ];
}
