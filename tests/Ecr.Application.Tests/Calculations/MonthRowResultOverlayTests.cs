// tests/Ecr.Application.Tests/Calculations/MonthRowResultOverlayTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Результати модуля «місяці в колонках» у рядках <c>F_&lt;X&gt;</c> зрізу сітки (HSE400,
/// <see cref="MonthRowResultOverlay"/>).
/// </summary>
public sealed class MonthRowResultOverlayTests
{
    private const long DocumentId = 41;
    private const int PeriodKeyValue = 202601;
    private const long InstanceId = 700;
    private const long ForeignInstanceId = 701;
    private const int TableDefId = 310;
    private const int AnchorColumnId = 9000;
    private const int JanuaryId = 9001;
    private const int FebruaryId = 9002;
    private const long InputRowId = 1;
    private const long GcvRowId = 2;
    private const long DensityRowId = 3;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private static readonly IReadOnlyDictionary<string, long> Rows =
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["CH4"] = InputRowId,
            ["F_GCV"] = GcvRowId,
            ["F_DENSITY"] = DensityRowId,
        };

    private static readonly IReadOnlyDictionary<string, int> Months =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["M01"] = JanuaryId, ["M02"] = FebruaryId };

    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();

    /// <remarks>Мутація: прибрати <c>overlaid.Add</c> в <c>ApplyAsync</c> — рядок F_GCV порожній.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Результат_модуля_лягає_в_рядок_F_у_колонку_свого_місяця()
    {
        Binding(versions: [5]);
        Results(new CalculationResultRow(5, "F_GCV", "M01", 38.5m, 8, null, InstanceId),
                new CalculationResultRow(5, "F_GCV", "M02", 39.1m, 8, null, InstanceId),
                new CalculationResultRow(5, "F_DENSITY", "M01", 0.72m, 9, null, InstanceId));

        var cells = await ApplyAsync([Cell(InputRowId, JanuaryId, 91m)]);

        Assert.Equal(38.5m, Value(cells, GcvRowId, JanuaryId));
        Assert.Equal(39.1m, Value(cells, GcvRowId, FebruaryId));
        Assert.Equal(0.72m, Value(cells, DensityRowId, JanuaryId));
        Assert.Null(Value(cells, DensityRowId, FebruaryId));

        var gcv = cells.Single(c => c.Address == new CellAddress(Period, GcvRowId, JanuaryId));
        Assert.True(gcv.Value.IsCalculated);
        Assert.Equal(8, gcv.Value.ValueUnitId);
        Assert.Equal(TableDefId, gcv.TableDefId);
    }

    /// <remarks>Мутація: накладати на всі рядки, а не лише на <c>F_</c> — ввід CH4 зникає або замінюється.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Рядки_вводу_не_зачіпаються_навіть_коли_результат_має_їхній_ключ()
    {
        Binding(versions: [5]);
        Results(new CalculationResultRow(5, "CH4", "M01", 1m, 8, null, InstanceId),
                new CalculationResultRow(5, "F_GCV", "M01", 38.5m, 8, null, InstanceId));

        var cells = await ApplyAsync([Cell(InputRowId, JanuaryId, 91m), Cell(InputRowId, AnchorColumnId, 3m)]);

        var input = cells.Single(c => c.Address == new CellAddress(Period, InputRowId, JanuaryId));
        Assert.Equal(91m, input.Value.ValueNumeric);
        Assert.False(input.Value.IsCalculated);
        Assert.Equal(3m, Value(cells, InputRowId, AnchorColumnId));
    }

    /// <remarks>Мутація: не відкидати збережену комірку рядка F_ — у зрізі дві комірки з однією адресою.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Збережене_в_рядку_F_значення_місячної_колонки_поступається_результату()
    {
        Binding(versions: [5]);
        Results(new CalculationResultRow(5, "F_GCV", "M01", 38.5m, 8, null, InstanceId));

        var cells = await ApplyAsync(
        [
            Cell(GcvRowId, JanuaryId, 1m),
            Cell(GcvRowId, FebruaryId, 2m),
            Cell(GcvRowId, AnchorColumnId, 7m),
        ]);

        Assert.Equal(cells.Count, cells.Select(c => c.Address).Distinct().Count());
        Assert.Equal(38.5m, Value(cells, GcvRowId, JanuaryId));

        // Місяць без результату: введене число — не результат, комірка порожня.
        Assert.Null(Value(cells, GcvRowId, FebruaryId));

        // Колонка поза місяцями — не наша.
        Assert.Equal(7m, Value(cells, GcvRowId, AnchorColumnId));
    }

    /// <remarks>Мутація: прибрати перевагу свого екземпляра — у комірку лягає число чужого аркуша-копії (D-401).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Свій_екземпляр_має_перевагу_а_чужий_ігнорується()
    {
        Binding(versions: [5]);
        Results(new CalculationResultRow(5, "F_GCV", "M01", 38.5m, 8, null, InstanceId),
                new CalculationResultRow(5, "F_GCV", "M01", 11m, 8, null),
                new CalculationResultRow(5, "F_GCV", "M01", 99m, 8, null, ForeignInstanceId),
                new CalculationResultRow(5, "F_GCV", "M02", 40m, 8, null),
                new CalculationResultRow(5, "F_DENSITY", "M01", 99m, 9, null, ForeignInstanceId));

        var cells = await ApplyAsync([]);

        Assert.Equal(38.5m, Value(cells, GcvRowId, JanuaryId));
        Assert.Equal(40m, Value(cells, GcvRowId, FebruaryId));
        Assert.Null(Value(cells, DensityRowId, JanuaryId));
    }

    /// <remarks>Мутація: брати перше значення без порівняння — комірка показує одне з двох різних чисел.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Різні_значення_однієї_комірки_лишають_її_порожньою_а_однакові_дають_одну()
    {
        Binding(versions: [5, 6]);
        Results(new CalculationResultRow(5, "F_GCV", "M01", 38.5m, 8, null, InstanceId),
                new CalculationResultRow(6, "F_GCV", "M01", 38.6m, 8, null, InstanceId),
                new CalculationResultRow(5, "F_GCV", "M02", 40m, 8, null, InstanceId),
                new CalculationResultRow(6, "F_GCV", "M02", 40m, 8, null, InstanceId));

        var cells = await ApplyAsync([]);

        Assert.Null(Value(cells, GcvRowId, JanuaryId));
        Assert.Equal(40m, Value(cells, GcvRowId, FebruaryId));
        Assert.Single(cells);
    }

    /// <remarks>Мутація: прибрати фільтр версій — у комірку лягає число методології, не прив'язаної до таблиці.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_версії_поза_прив_язками_таблиці_не_береться()
    {
        Binding(versions: [5]);
        Results(new CalculationResultRow(77, "F_GCV", "M01", 38.5m, 8, null, InstanceId));

        var cells = await ApplyAsync([]);

        Assert.Empty(cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_без_місячних_колонок_або_без_рядків_F_не_коштує_жодного_запиту()
    {
        IReadOnlyList<CellRecord> stored = [Cell(InputRowId, JanuaryId, 91m)];
        var overlay = new MonthRowResultOverlay(_methodologies, _results);

        var noMonths = await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue, InstanceId, TableDefId, Rows, stored,
            new Dictionary<string, int>(), CancellationToken.None);
        var noCalculatedRows = await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue, InstanceId, TableDefId,
            new Dictionary<string, long> { ["CH4"] = InputRowId, ["F_"] = 9, ["FUEL"] = 10 }, stored,
            Months, CancellationToken.None);

        Assert.Same(stored, noMonths);
        Assert.Same(stored, noCalculatedRows);
        Assert.Empty(_methodologies.ReceivedCalls());
        Assert.Empty(_results.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_без_прив_язок_лишає_комірки_як_є_і_результатів_не_читає()
    {
        _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        IReadOnlyList<CellRecord> stored = [Cell(GcvRowId, JanuaryId, 1m)];

        var cells = await new MonthRowResultOverlay(_methodologies, _results).ApplyAsync(
            DocumentId, PeriodKeyValue, InstanceId, TableDefId, Rows, stored, Months, CancellationToken.None);

        Assert.Same(stored, cells);
        Assert.Empty(_results.ReceivedCalls());
    }

    /// <remarks>
    /// Перф: вузьке читання (свій екземпляр, коди місяців, версії прив'язок) замість усіх результатів документа.
    /// Мутація: завжди кликати <c>ReadCurrentAsync</c> — перевірка виклику червона.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Сховище_з_вузьким_читанням_отримує_фільтр_екземпляра_місяців_і_версій()
    {
        Binding(versions: [5, 6]);
        var store = Substitute.For<ICalculationResultStore, ICurrentResultFilterReader>();
        CurrentResultFilter? seen = null;
        ((ICurrentResultFilterReader)store)
            .ReadCurrentMatchingAsync(DocumentId, PeriodKeyValue, Arg.Do<CurrentResultFilter>(f => seen = f), Arg.Any<CancellationToken>())
            .Returns([new CalculationResultRow(6, "F_GCV", "M02", 40m, 8, null, InstanceId)]);

        var cells = await new MonthRowResultOverlay(_methodologies, store).ApplyAsync(
            DocumentId, PeriodKeyValue, InstanceId, TableDefId, Rows, [], Months, CancellationToken.None);

        Assert.Equal(40m, Value(cells, GcvRowId, FebruaryId));
        Assert.NotNull(seen);
        Assert.Equal(new[] { InstanceId }, seen.TableInstanceIds.ToArray());
        Assert.Equal("M01,M02", string.Join(',', seen.OutputCodes.Order(StringComparer.Ordinal)));
        Assert.Equal("5,6", string.Join(',', seen.MethodologyVersionIds.Order()));
        await store.DidNotReceive().ReadCurrentAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("F_GCV", true)]
    [InlineData("f_gcv", true)]
    [InlineData("F_", false)]
    [InlineData("FUEL", false)]
    [InlineData("CH4", false)]
    [InlineData("X_F_GCV", false)]
    public void Розрахунковий_рядок_упізнається_за_префіксом_F(string rowKey, bool expected)
        => Assert.Equal(expected, MonthRowResultOverlay.IsCalculatedRow(rowKey));

    /// <remarks>
    /// Мутація: позначати без огляду на причину — відмова «період закрито»/«немає гранта» підміняється
    /// «обчислюваною коміркою», і сітка бреше про причину блокування.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Комірки_рядків_F_у_місячних_колонках_стають_обчислюваними_а_інші_рішення_не_змінюються()
    {
        var readOnlyRow = new CellAddress(Period, GcvRowId, JanuaryId);
        var allowed = new CellAddress(Period, GcvRowId, FebruaryId);
        var closed = new CellAddress(Period, DensityRowId, JanuaryId);
        var anchor = new CellAddress(Period, GcvRowId, AnchorColumnId);
        var input = new CellAddress(Period, InputRowId, JanuaryId);
        var decisions = new Dictionary<CellAddress, EditDecision>
        {
            [readOnlyRow] = EditDecision.Deny(EditDenyReason.RowReadOnly),
            [allowed] = new EditDecision(true, EditDenyReason.None),
            [closed] = EditDecision.Deny(EditDenyReason.PeriodClosed),
            [anchor] = EditDecision.Deny(EditDenyReason.RowReadOnly),
            [input] = new EditDecision(true, EditDenyReason.None),
        };

        var marked = MonthRowResultOverlay.MarkCalculated(decisions, Rows, Months);

        Assert.Equal(decisions.Count, marked.Count);
        Assert.Equal(EditDenyReason.CalculatedCell, marked[readOnlyRow].Reason);
        Assert.False(marked[readOnlyRow].IsAllowed);
        Assert.Equal(EditDenyReason.CalculatedCell, marked[allowed].Reason);
        Assert.False(marked[allowed].IsAllowed);
        Assert.Equal(EditDenyReason.PeriodClosed, marked[closed].Reason);
        Assert.Equal(EditDenyReason.RowReadOnly, marked[anchor].Reason);
        Assert.True(marked[input].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Таблиця_без_рядків_F_або_місячних_колонок_повертає_ті_самі_рішення()
    {
        IReadOnlyDictionary<CellAddress, EditDecision> decisions = new Dictionary<CellAddress, EditDecision>
        {
            [new CellAddress(Period, InputRowId, JanuaryId)] = new EditDecision(true, EditDenyReason.None),
        };

        Assert.Same(decisions, MonthRowResultOverlay.MarkCalculated(decisions, Rows, new Dictionary<string, int>()));
        Assert.Same(decisions, MonthRowResultOverlay.MarkCalculated(
            decisions, new Dictionary<string, long> { ["CH4"] = InputRowId }, Months));
    }

    private void Binding(int[] versions)
        => _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableDefId, AnchorColumnId, 2, "M01", "{}", versions)]);

    private void Results(params CalculationResultRow[] rows)
        => _results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>()).Returns(rows);

    private Task<IReadOnlyList<CellRecord>> ApplyAsync(IReadOnlyList<CellRecord> stored)
        => new MonthRowResultOverlay(_methodologies, _results).ApplyAsync(
            DocumentId, PeriodKeyValue, InstanceId, TableDefId, Rows, stored, Months, CancellationToken.None);

    private static CellRecord Cell(long rowId, int columnId, decimal value)
        => new(new CellAddress(Period, rowId, columnId), TableDefId, new CellValueData { ValueNumeric = value });

    private static decimal? Value(IReadOnlyList<CellRecord> cells, long rowId, int columnId)
        => cells.SingleOrDefault(c => c.Address == new CellAddress(Period, rowId, columnId))?.Value.ValueNumeric;
}
