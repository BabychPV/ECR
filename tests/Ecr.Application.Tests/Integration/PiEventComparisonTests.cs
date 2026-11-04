// tests/Ecr.Application.Tests/Integration/PiEventComparisonTests.cs
using Ecr.Application.Integration.RawData;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// <c>D-465</c>: порівняння «PI каже / у комірці» без бази — кожен стан і кожна причина, курсор і CSV.
/// </summary>
public sealed class PiEventComparisonTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Start = new(2026, 3, 10, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sync = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly SourceEventFieldPlan StartField = new(
        1, "START", CellDataType.Date, SourceEventMap.StartAttribute, SourceEventAttributeScope.Event, SourceEventValueKind.Direct,
        null, null, [], new Dictionary<string, long>());

    private static readonly SourceEventFieldPlan VolumeField = new(
        2, "VOL", CellDataType.Decimal, "Volume", SourceEventAttributeScope.Event, SourceEventValueKind.Direct,
        null, null, [], new Dictionary<string, long>());

    private static readonly SourceEventFieldPlan CategoryField = new(
        3, "CAT", CellDataType.Lookup, "Category", SourceEventAttributeScope.Event, SourceEventValueKind.LookupByCode,
        null, null, [new SourceEventLookupEntry(77, "SWEET", ["Sweet"])], new Dictionary<string, long>());

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Збіг_ручна_правка_не_оновлено_й_без_значення_в_PI()
    {
        var link = Link(row: true);
        var values = new[] { Value(link, "Volume", 3.5m), Value(link, "Category", null, "sweet") };

        var match = Compare(link, Context(), values, Cells(volume: 3.5m, category: 77));
        Assert.Equal("Match", match.Single(a => a.ColumnCode == "VOL").Comparison);
        Assert.Equal("Match", match.Single(a => a.ColumnCode == "CAT").Comparison);
        Assert.Equal("SWEET", match.Single(a => a.ColumnCode == "CAT").ExpectedValue);
        Assert.Equal("Match", match.Single(a => a.ColumnCode == "START").Comparison);

        var manual = Compare(link, Context(), values, Cells(volume: 9m, category: 77, origin: "UserEdit"));
        var volume = manual.Single(a => a.ColumnCode == "VOL");
        Assert.Equal(("ManualEdit", "KeptManual"), (volume.Comparison, volume.Reason));
        Assert.Equal("9", volume.CellValue);
        Assert.Equal("3.5", volume.ExpectedValue);

        var stale = Compare(link, Context(), values, Cells(volume: 9m, category: 77, origin: "Integration"));
        Assert.Equal(("NotUpdated", "Pending"), (stale.Single(a => a.ColumnCode == "VOL").Comparison, stale.Single(a => a.ColumnCode == "VOL").Reason));

        var none = Compare(link, Context(), [], Cells(volume: 9m, category: 77));
        Assert.Equal("NoSourceValue", none.Single(a => a.ColumnCode == "VOL").Comparison);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Причини_незапису_за_станом_звʼязку_періоду_й_аркуша()
    {
        var values = new[] { Value(Link(row: false), "Volume", 1m) };

        Assert.Equal("Open", Reason(Link(row: false, SourceEventLinkStatus.Open, end: false), Context()));
        Assert.Equal("OpenAtPeriodClose", Reason(Link(row: false, SourceEventLinkStatus.Open, end: false), Context(PeriodState.Closed)));
        Assert.Equal("PeriodClosed", Reason(Link(row: false, SourceEventLinkStatus.PeriodClosed), Context(PeriodState.Closed)));
        Assert.Equal("PeriodNotOpen", Reason(Link(row: false, SourceEventLinkStatus.PeriodNotOpen), Context()));
        Assert.Equal("RowLimit", Reason(Link(row: false, SourceEventLinkStatus.RowLimit), Context()));
        Assert.Equal("SheetLocked", Reason(Link(row: false, SourceEventLinkStatus.PeriodNotOpen), Context(locked: true)));
        Assert.Equal("PeriodChanged", Reason(Link(row: true, SourceEventLinkStatus.PeriodChanged), Context()));
        Assert.Equal("Missing", Reason(Link(row: true, SourceEventLinkStatus.Missing), Context()));
        Assert.Equal("SheetLocked", Reason(Link(row: true), Context(locked: true)));
        Assert.Equal("PeriodClosed", Reason(Link(row: true), Context(PeriodState.Closed)));
        Assert.Null(Reason(Link(row: true), Context()));

        // Рядка немає — кожне поле «не записано» з причиною події.
        var unwritten = Compare(Link(row: false, SourceEventLinkStatus.RowLimit), Context(), values, null);
        Assert.Equal(("NotWritten", "RowLimit"), (unwritten.Single(a => a.ColumnCode == "VOL").Comparison, unwritten.Single(a => a.ColumnCode == "VOL").Reason));

        // Аркуш поданий, у комірці інше — «не записано, аркуш поданий», а не «ручна правка».
        var locked = Compare(Link(row: true), Context(locked: true), [Value(Link(row: true), "Volume", 2m)], Cells(volume: 1m, category: null));
        Assert.Equal(("NotWritten", "SheetLocked"), (locked.Single(a => a.ColumnCode == "VOL").Comparison, locked.Single(a => a.ColumnCode == "VOL").Reason));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Незіставлене_приховане_й_задовгий_текст()
    {
        var link = Link(row: true) with { UnmappedJson = "[{\"column\":\"CAT\",\"value\":\"Sour\"}]" };
        var values = new[] { Value(link, "Volume", 1m), Value(link, "Category", null, "Sour"), Value(link, "Filter", null, "x") };

        var result = Compare(link, Context(), values, Cells(volume: 1m, category: null));
        Assert.Equal(("NotWritten", "Unmapped"), (result.Single(a => a.ColumnCode == "CAT").Comparison, result.Single(a => a.ColumnCode == "CAT").Reason));

        // Атрибут без поля мапінгу — лише значення PI, без порівняння.
        var extra = result.Single(a => a.Attribute == "Filter");
        Assert.Null(extra.ColumnDefId);
        Assert.Null(extra.Comparison);

        // Документ невидимий — «приховано», значення комірки не віддається.
        var hidden = PiEventComparison.Compare(link, Context(), Utc, values, null, _ => true);
        Assert.All(hidden.Where(a => a.ColumnDefId is not null), a => Assert.Equal("Hidden", a.Comparison));
        Assert.All(hidden, a => Assert.Null(a.CellValue));

        // Колонка закрита Deny — теж приховано.
        var deniedCells = Cells(volume: 1m, category: null);
        var denied = PiEventComparison.Compare(link, Context(), Utc, values, column => deniedCells.FirstOrDefault(c => c.ColumnDefId == column), c => c != 2);
        Assert.Equal("Hidden", denied.Single(a => a.ColumnCode == "VOL").Comparison);
        Assert.Null(denied.Single(a => a.ColumnCode == "VOL").CellValue);

        var truncated = Compare(link, Context(), [Value(link, "Volume", null, new string('9', 4000)) with { Truncated = true }], Cells(volume: 1m, category: null));
        Assert.Equal("NotComparable", truncated.Single(a => a.ColumnCode == "VOL").Comparison);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Курсор_туди_й_назад_зіпсований_початок_і_CSV_без_формул()
    {
        var cursor = PiEventDataHandler.EncodeCursor(Start, 42);
        Assert.Equal(new PiEventCursor(Start, 42), PiEventDataHandler.DecodeCursor(cursor));
        Assert.Null(PiEventDataHandler.DecodeCursor("@@@"));
        Assert.Null(PiEventDataHandler.DecodeCursor(Convert.ToBase64String("t:1"u8.ToArray())));

        var link = Link(row: true) with { EventName = "=cmd|' /c calc'!A1" };
        var attribute = new PiEventAttributeView(
            2, "VOL", "Volume", SourceEventAttributeScope.Event, -5m, null, false, "@unit", null, "-5", "-5", "Integration", "Match", null);
        var view = PiEventDataHandler.ToView(
            new PiEventLink(1, 1, "E1", link.EventName, Start, Start.AddHours(1), null, null, SourceEventLinkStatus.Synced, 202601, 10, "EF-E1", null, null, Sync, Sync, Sync),
            new PiEventMap(1, 1, "T", null, 5, "DOC", 1, "TBL", "UTC", true, []),
            Context(),
            Utc,
            documentVisible: true,
            [attribute]);

        var line = PiEventDataHandler.ToCsv(new PiEventCsvRow(view, attribute));
        Assert.Contains(",'=cmd|' /c calc'!A1,", line, StringComparison.Ordinal);
        Assert.Contains(",-5,", line, StringComparison.Ordinal);
        Assert.Contains(",'@unit,", line, StringComparison.Ordinal);
        Assert.Contains(",-5,-5,", line, StringComparison.Ordinal);
    }

    private static string? Reason(PiEventLink link, PiEventMapContext context) => PiEventComparison.LinkReason(link, context)?.ToString();

    private static IReadOnlyList<PiEventAttributeView> Compare(
        PiEventLink link, PiEventMapContext context, IReadOnlyList<PiObservedValue> values, IReadOnlyList<PiCellValue>? cells)
        => PiEventComparison.Compare(
            link,
            context,
            Utc,
            values,
            cells is null ? null : column => cells.FirstOrDefault(c => c.ColumnDefId == column),
            _ => true);

    private static PiEventMapContext Context(PeriodState state = PeriodState.Open, bool locked = false)
        => new(
            [StartField, VolumeField, CategoryField],
            new Dictionary<int, byte?> { [2] = 4 },
            null,
            [new SourceEventPeriod(202601, state, new Period.UtcRange(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)))],
            locked ? new HashSet<int> { 202601 } : new HashSet<int>());

    private static PiEventLink Link(bool row, SourceEventLinkStatus status = SourceEventLinkStatus.Synced, bool end = true)
        => new(
            1, 1, "E1", "Flaring", Start, end ? Start.AddHours(1) : null, Start, "UNIT-1", status,
            row ? 202601 : null, row ? 10 : null, row ? "EF-E1" : null, null, null, Sync, Sync, Sync);

    private static PiObservedValue Value(PiEventLink link, string attribute, decimal? number, string? text = null)
        => new(link.Id, SourceEventAttributeScope.Event, attribute, number, text, false, null, null, Sync.AddDays(1));

    private static IReadOnlyList<PiCellValue> Cells(decimal? volume, long? category, string origin = "Integration")
        =>
        [
            new(202601, 10, "EF-E1", 1, true, null, null, Start, null, origin, Sync),
            new(202601, 10, "EF-E1", 2, volume is not null, volume, null, null, null, origin, Sync),
            new(202601, 10, "EF-E1", 3, category is not null, null, null, null, category, origin, Sync),
        ];
}
