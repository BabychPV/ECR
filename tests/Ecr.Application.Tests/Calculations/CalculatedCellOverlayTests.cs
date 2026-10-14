// tests/Ecr.Application.Tests/Calculations/CalculatedCellOverlayTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Розв'язання посилання «колонка Calculated → результат методології»
/// (<see cref="CalculatedCellOverlay"/>, F-02 і F-09 четвертого раунду UX).
/// </summary>
public sealed class CalculatedCellOverlayTests
{
    private const long DocumentId = 19;
    private const int PeriodKeyValue = 202609;
    private const long InstanceId = 500;
    private const int TableDefId = 193;
    private const int InputColumnId = 6015;
    private const int EmissionColumnId = 6017;
    private const int MethodologyId = 2;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();

    private static readonly IReadOnlyDictionary<string, long> Rows =
        new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = 1, ["R2"] = 2 };

    /// <remarks>Мутація: прибрати додавання комірки в <c>ApplyAsync</c> — EMISSION порожня.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_актуального_прогону_лягає_в_колонку_Calculated_за_ключем_рядка()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null),
                new CalculationResultRow(5, "R2", "EMISSION", 12.5m, 8, null));

        var cells = await ApplyAsync([Cell(1, InputColumnId, 8m), Cell(2, InputColumnId, 5m)]);

        Assert.Equal(20m, Emission(cells, 1));
        Assert.Equal(12.5m, Emission(cells, 2));
        Assert.All(cells.Where(c => c.Address.ColumnDefId == EmissionColumnId), c => Assert.True(c.Value.IsCalculated));

        // Введені комірки на місці.
        Assert.Equal(2, cells.Count(c => c.Address.ColumnDefId == InputColumnId));
    }

    /// <remarks>
    /// F-09: предикат прив'язки справді звужує рядки. Мутація: прибрати перевірку
    /// <c>MethodologyRuleMatcher.Matches</c> — R2 теж отримує число.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Предикат_прив_язки_звужує_рядки_що_отримують_число()
    {
        Binding($$"""{"{{InputColumnId}}":"8"}""", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null),
                new CalculationResultRow(5, "R2", "EMISSION", 12.5m, 8, null));

        var cells = await ApplyAsync([Cell(1, InputColumnId, 8m), Cell(2, InputColumnId, 5m)]);

        Assert.Equal(20m, Emission(cells, 1));
        Assert.Null(Emission(cells, 2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Число_чужої_методології_й_чужого_виходу_не_береться_а_речовини_сумуються()
    {
        Binding("{}", versions: [5, 6]);
        Results(new CalculationResultRow(6, "R1", "EMISSION", 1m, 8, 901),
                new CalculationResultRow(6, "R1", "EMISSION", 2m, 8, 902),
                new CalculationResultRow(99, "R2", "EMISSION", 7m, 8, null),
                new CalculationResultRow(5, "R2", "OTHER", 7m, 8, null));

        var cells = await ApplyAsync([]);

        Assert.Equal(3m, Emission(cells, 1));
        Assert.Null(Emission(cells, 2));
    }

    /// <remarks>
    /// P2-1: дві активні прив'язки на ОДНУ колонку давали дві комірки з тією самою адресою, і
    /// <c>GetTableSliceHandler</c> падав на <c>ToDictionary</c> (500). Мутація: прибрати <c>filled</c> —
    /// у (рядок, колонка) знову дві комірки.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Дві_прив_язки_на_одну_колонку_дають_одну_комірку_від_найбільшого_Id()
    {
        _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new ColumnResultBinding(TableDefId, EmissionColumnId, MethodologyId, "EMISSION", "{}", [5]),
                new ColumnResultBinding(TableDefId, EmissionColumnId, 3, "GSEC", "{}", [7]),
            ]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null),
                new CalculationResultRow(7, "R1", "GSEC", 4m, 8, null),
                new CalculationResultRow(5, "R2", "EMISSION", 12.5m, 8, null));

        var cells = await ApplyAsync([]);

        // Один рядок — одна комірка колонки; ключ словника зрізу не дублюється.
        Assert.Equal(cells.Count, cells.Select(c => (c.Address.TableRowId, c.Address.ColumnDefId)).Distinct().Count());
        Assert.Equal(4m, Emission(cells, 1));       // R1: пізніша (GSEC) виграє, не сума 24
        Assert.Equal(12.5m, Emission(cells, 2));    // R2: пізніша числа не має — діє рання
    }

    /// <remarks>
    /// D-401 (тимчасовий захід): два рядки результату з тим самим <c>RowKey</c>, виходом і речовиною, але
    /// різними значеннями — це рядки ДВОХ таблиць з однаковим ключем. Сума була б хибним числом.
    /// Мутація: повернути <c>values.Sum</c> замість <c>TryCombine</c> — комірка = 15, а не порожня.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Два_різні_значення_однієї_речовини_не_сумуються_комірка_порожня_і_є_попередження()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "M01", "EMISSION", 10m, 8, null),
                new CalculationResultRow(5, "M01", "EMISSION", 5m, 8, null),
                new CalculationResultRow(5, "M02", "EMISSION", 7m, 8, null));
        var log = new CapturingLogger();

        var cells = await ApplyAsync([], log, "M01", "M02");

        Assert.Null(Emission(cells, 1));
        Assert.Equal(7m, Emission(cells, 2));
        var warning = Assert.Single(log.Messages);
        Assert.Contains("M01/EMISSION", warning, StringComparison.Ordinal);
    }

    /// <remarks>Мутація: прибрати групування за речовиною — подвоєне значення 40 замість 20.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Однакові_дублікати_однієї_речовини_дають_одне_значення_а_не_суму()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null),
                new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null));

        var cells = await ApplyAsync([]);

        Assert.Equal(20m, Emission(cells, 1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Дублікати_кожної_з_двох_речовин_збігаються_за_значенням_і_сума_речовин_лишається()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 1m, 8, 901),
                new CalculationResultRow(5, "R1", "EMISSION", 1m, 8, 901),
                new CalculationResultRow(5, "R1", "EMISSION", 2m, 8, 902),
                new CalculationResultRow(5, "R1", "EMISSION", 2m, 8, 902));

        var cells = await ApplyAsync([]);

        Assert.Equal(3m, Emission(cells, 1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Колізія_однієї_речовини_робить_порожньою_всю_комірку_навіть_коли_друга_речовина_чиста()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 1m, 8, 901),
                new CalculationResultRow(5, "R1", "EMISSION", 4m, 8, 901),
                new CalculationResultRow(5, "R1", "EMISSION", 2m, 8, 902));

        var cells = await ApplyAsync([]);

        Assert.Null(Emission(cells, 1));
    }

    /// <remarks>
    /// T1 (D-401): дві таблиці одного документа з однаковим <c>RowKey</c> і спільною прив'язкою — кожна
    /// отримує СВОЄ число за екземпляром. Мутація: прибрати відбір за <c>TableInstanceId</c> — 30 (сума) або порожньо.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task T1_Дві_таблиці_з_однаковим_RowKey_і_спільною_прив_язкою_кожна_бере_своє_число()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 10m, 8, null, InstanceId),
                new CalculationResultRow(5, "R1", "EMISSION", 20m, 8, null, InstanceId + 1));
        var overlay = new CalculatedCellOverlay(_methodologies, _results);
        IReadOnlySet<int> calculated = new HashSet<int> { EmissionColumnId };

        var result = await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue,
            [
                new OverlayTable(InstanceId, TableDefId, Rows, [], calculated),
                new OverlayTable(InstanceId + 1, TableDefId, Rows, [], calculated),
            ],
            CancellationToken.None);

        Assert.Equal(10m, Emission(result[InstanceId], 1));
        Assert.Equal(20m, Emission(result[InstanceId + 1], 1));
    }

    /// <remarks>Мутація: брати B разом з A — 10 + 3 = 13.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Результат_свого_екземпляра_має_перевагу_над_старим_без_екземпляра_а_чужий_ігнорується()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 10m, 8, null, InstanceId),
                new CalculationResultRow(5, "R1", "EMISSION", 3m, 8, null),
                new CalculationResultRow(5, "R2", "EMISSION", 99m, 8, null, InstanceId + 7));

        var cells = await ApplyAsync([]);

        Assert.Equal(10m, Emission(cells, 1));  // A виграє над B
        Assert.Null(Emission(cells, 2));        // C — чужий екземпляр
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Старий_результат_без_екземпляра_діє_за_тимчасовим_правилом()
    {
        Binding("{}", versions: [5]);
        Results(new CalculationResultRow(5, "R1", "EMISSION", 4m, 8, null));

        var cells = await ApplyAsync([]);

        Assert.Equal(4m, Emission(cells, 1));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Таблиця_без_колонки_Calculated_не_робить_жодного_запиту()
    {
        var overlay = new CalculatedCellOverlay(_methodologies, _results);

        await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue,
            [new OverlayTable(InstanceId, TableDefId, Rows, [], new HashSet<int>())],
            CancellationToken.None);

        await _methodologies.DidNotReceiveWithAnyArgs().GetColumnResultBindingsAsync(default!, default);
        await _results.DidNotReceiveWithAnyArgs().ReadCurrentAsync(default, default, default);
    }

    private async Task<IReadOnlyList<CellRecord>> ApplyAsync(
        IReadOnlyList<CellRecord> cells,
        Microsoft.Extensions.Logging.ILogger<CalculatedCellOverlay>? log = null,
        params string[] rowKeys)
    {
        var overlay = new CalculatedCellOverlay(_methodologies, _results, log);
        var rows = rowKeys.Length == 0
            ? Rows
            : rowKeys.Select((k, i) => (k, id: (long)i + 1)).ToDictionary(x => x.k, x => x.id, StringComparer.Ordinal);

        var result = await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue,
            [new OverlayTable(InstanceId, TableDefId, rows, cells, new HashSet<int> { EmissionColumnId })],
            CancellationToken.None);

        return result[InstanceId];
    }

    /// <summary>Збирає відформатовані повідомлення журналу (рівень Warning).</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<CalculatedCellOverlay>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private void Binding(string matchJson, IReadOnlyList<int> versions)
        => _methodologies.GetColumnResultBindingsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new ColumnResultBinding(TableDefId, EmissionColumnId, MethodologyId, "EMISSION", matchJson, versions)]);

    private void Results(params CalculationResultRow[] rows)
        => _results.ReadCurrentAsync(DocumentId, PeriodKeyValue, Arg.Any<CancellationToken>()).Returns(rows);

    private static CellRecord Cell(long rowId, int columnId, decimal value)
        => new(new CellAddress(Period, rowId, columnId), TableDefId, new CellValueData { ValueNumeric = value });

    private static decimal? Emission(IReadOnlyList<CellRecord> cells, long rowId)
        => cells.FirstOrDefault(c => c.Address.TableRowId == rowId && c.Address.ColumnDefId == EmissionColumnId)
            ?.Value.ValueNumeric;
}
