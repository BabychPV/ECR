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

    private async Task<IReadOnlyList<CellRecord>> ApplyAsync(IReadOnlyList<CellRecord> cells)
    {
        var overlay = new CalculatedCellOverlay(_methodologies, _results);

        var result = await overlay.ApplyAsync(
            DocumentId, PeriodKeyValue,
            [new OverlayTable(InstanceId, TableDefId, Rows, cells, new HashSet<int> { EmissionColumnId })],
            CancellationToken.None);

        return result[InstanceId];
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
