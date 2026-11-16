using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ B131 (D-491): новий рядок PATCH (і тіньовий рядок власника Q-17) дає насіння перерахунку й у колонках із
/// придатним дефолтом, навіть незаписаних: дефолт входить в агрегати, тож склад рядків їх змінює. Таблиця без дефолтів
/// — насіння лише записаних комірок, як і доти.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітити): (1) у <c>BuildRecalculationSeeds</c> прибрати <c>Concat(NewRowDefaultSeeds…)</c> —
/// червоніє «новий рядок PATCH»; (2) у <c>NewRowDefaultSeeds</c> брати всі рядки батчу (<c>Touched</c>) замість нових —
/// червоніє «наявний рядок»; (3) у <c>RouteForeignOwnersAsync</c> не додавати <c>shadowSeeds</c> — червоніє «тіньовий
/// рядок власника». Умову придатності дефолту (<c>Parse</c>) тримає <c>PatchCellsWorkbookTests</c> (рядок «abc»).
/// </remarks>
public sealed partial class PatchCellsTests
{
    private const long NewViewRow = 3001;
    private const long ShadowRow = 4001;

    /// <summary>Усі payload задач формул: екземпляр → адреси насіння.</summary>
    private List<(long Instance, List<(long Row, int Column)> Cells)> FormulaPayloads()
        => [.. _jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .Select(c => c.GetArguments()[1]!)
            .Select(p => (
                (long)p.GetType().GetProperty("TableInstanceId")!.GetValue(p)!,
                ((IEnumerable<RecalculationSeed>)p.GetType().GetProperty("Cells")!.GetValue(p)!)
                    .Select(s => (s.RowId, s.ColumnDefId)).ToList()))];

    /// <summary>Дефолт колонки <c>RegistryLink</c>/<c>SiteLink</c> знімка основного набору.</summary>
    private async Task SetDefaultAsync(int columnId, string? value)
        => (await _metadata.GetAsync(2, CancellationToken.None)).ColumnsById[columnId]
            .SetPresentation(value, displayFormat: null, styleId: null);

    private void NewRowCreated()
        => _rows.CreateRowsAsync(
                TableInstance, Arg.Any<PeriodKey>(), Arg.Any<IReadOnlyList<RowKey>>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<long> { NewViewRow });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B131")]
    public async Task B131_новий_рядок_PATCH_дає_насіння_колонки_з_дефолтом_яку_не_писали()
    {
        await SetDefaultAsync(RegistryLinkColumnId, "CODE-1");
        NewRowCreated();

        await Handler().HandleAsync(
            Request(new PatchRow("NEW-1", BaseVersion: null, [new PatchCell("Volume", 2m)])),
            CancellationToken.None);

        var cells = Assert.Single(FormulaPayloads()).Cells;
        Assert.Contains((NewViewRow, VolumeColumnId), cells);
        Assert.Contains((NewViewRow, RegistryLinkColumnId), cells);
        Assert.Equal(cells.Count, cells.Distinct().Count());
    }

    /// <summary>Таблиця без придатних дефолтів (немає / пробіли / текст у числовій) — насіння лише записаних.</summary>
    /// <param name="value">Дефолт числової колонки Volume (Lookup-колонки — без дефолту).</param>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B131")]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("abc")]
    public async Task B131_без_придатного_дефолту_насіння_лише_записаних_комірок(string? value)
    {
        await SetDefaultAsync(VolumeColumnId, value);
        NewRowCreated();

        await Handler().HandleAsync(
            Request(new PatchRow("NEW-1", BaseVersion: null, [new PatchCell("Volume", 2m)])),
            CancellationToken.None);

        Assert.Equal([(NewViewRow, VolumeColumnId)], Assert.Single(FormulaPayloads()).Cells);
    }

    /// <summary>
    /// Правка НАЯВНОГО рядка таблиці з дефолтом — склад не змінився, зайвого насіння немає; у ЗМІШАНОМУ батчі (B132:
    /// наявний + новий рядок) насіння дефолту — лише в нового. Мутація N4: <c>NewRowIds</c> з усіх рядків батчу
    /// (<c>Touched</c>) — з'являється (1001, RegistryLink), обидва рядки червоні.
    /// </summary>
    /// <param name="mixed">Додати в батч новий рядок.</param>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "D-491")]
    [Trait("Finding", "B131")]
    [Trait("Finding", "B132")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B131_наявний_рядок_не_дає_насіння_дефолтів(bool mixed)
    {
        await SetDefaultAsync(RegistryLinkColumnId, "CODE-1");
        NewRowCreated();

        List<PatchRow> rows = [new PatchRow("7001001", "0x0A", [new PatchCell("Volume", 12501m)])];
        if (mixed)
        {
            rows.Add(new PatchRow("NEW-1", BaseVersion: null, [new PatchCell("Volume", 2m)]));
        }

        await Handler().HandleAsync(Request([.. rows]), CancellationToken.None);

        List<(long, int)> expected = mixed
            ? [(1001L, VolumeColumnId), (NewViewRow, VolumeColumnId), (NewViewRow, RegistryLinkColumnId)]
            : [(1001L, VolumeColumnId)];
        Assert.Equal(expected.Order(), Assert.Single(FormulaPayloads()).Cells.Order());
    }

    /// <summary>
    /// Q-17: новий рядок з перегляду вересня пише Category (власник — якір 202601), у якорі рядка немає — сховище
    /// створює тіньовий. Fuel (власник — якір) має дефолт: задача ЕКЗЕМПЛЯРА ВЛАСНИКА несе й (тіньовий рядок × Fuel),
    /// задача перегляду — (новий рядок × Fuel).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "Q-17")]
    [Trait("Finding", "B131")]
    public async Task B131_тіньовий_рядок_власника_дає_насіння_колонки_з_дефолтом_у_екземплярі_власника()
    {
        RoutedTable(requiredColumnId: 12, routing: true);
        (await _metadata.GetAsync(2, CancellationToken.None)).ColumnsById[FuelColumnId]
            .SetPresentation("Petrol", displayFormat: null, styleId: null);
        NewRowCreated();
        _access.CanCreateRowsAsync(
                   Arg.Any<Ecr.Application.Security.AccessProfile>(), TableInstance,
                   Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
               .Returns(call => call.ArgAt<IReadOnlyCollection<string>>(2).ToDictionary(
                   k => k,
                   _ => new Ecr.Application.Security.NewRowAccess(
                       Ecr.Application.Security.EditDecision.Allow(),
                       new Dictionary<int, Ecr.Application.Security.EditDecision>
                       {
                           [12] = Ecr.Application.Security.EditDecision.Allow(),
                       }),
                   StringComparer.Ordinal));
        _rows.GetMaxOrdinalAsync(OwnerInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(1);
        _rows.CreateRowsBatchAsync(Arg.Any<IReadOnlyList<RowCreationBatch>>(), Arg.Any<CancellationToken>())
            .Returns(new List<IReadOnlyList<long>> { new List<long> { ShadowRow } });

        await Handler().HandleAsync(
            new PatchCellsRequest(
                TableInstance, ViewPeriod, "UserEdit",
                [new PatchRow("NEW-1", BaseVersion: null, [new PatchCell("Category", "Engine")])]),
            CancellationToken.None);

        var payloads = FormulaPayloads();
        var owner = Assert.Single(payloads, p => p.Instance == OwnerInstance).Cells;
        Assert.Contains((ShadowRow, 12), owner);
        Assert.Contains((ShadowRow, FuelColumnId), owner);

        var view = Assert.Single(payloads, p => p.Instance == TableInstance).Cells;
        Assert.Contains((NewViewRow, FuelColumnId), view);
    }
}
