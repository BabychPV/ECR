using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>ФВ-2.6/2.7: слайс віддає результат правил умовного форматування по комірках.</summary>
public sealed class TableSliceConditionalFormatTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int VolumeId = 11;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IConditionalFormatStore _formats = Substitute.For<IConditionalFormatStore>();

    public TableSliceConditionalFormatTests()
    {
        var volume = new ColumnDef(TableDef, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(volume);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, TableDef, 2, Period));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(
        [
            new CellRecord(new CellAddress(new PeriodKey(Period), 1, VolumeId), TableDef, new CellValueData { ValueNumeric = 150m }),
            new CellRecord(new CellAddress(new PeriodKey(Period), 2, VolumeId), TableDef, new CellValueData { ValueNumeric = 5m }),
        ]);

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = volume },
                new Dictionary<(int, string), RowDef>()));

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());

        _formats.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            Task.FromResult<IReadOnlyList<ConditionalFormatRule>>(
            [
                new ConditionalFormatRule(2, "Volume", 1, "gt", "100", null, "#ff0000", "#ffffff", true),
                new ConditionalFormatRule(2, "Volume", 2, "empty", null, null, "#cccccc", null, false),
                new ConditionalFormatRule(2, "Other", 1, "gt", "0", null, "#00ff00", null, false),
            ]));
    }

    [Fact]
    public async Task Slice_returns_format_per_matching_cell_including_empty()
    {
        var slice = await Handler(_formats).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(2, slice.CellFormats!.Count);
        Assert.Equal(new CellFormatDto("#ff0000", "#ffffff", true), slice.CellFormats["R1:Volume"]);
        Assert.Equal(new CellFormatDto("#cccccc", null, false), slice.CellFormats["R3:Volume"]);
        Assert.False(slice.CellFormats.ContainsKey("R2:Volume"));
        await _formats.Received(1).GetAsync(2, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slice_without_store_has_empty_formats()
    {
        var slice = await Handler(null).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Empty(slice.CellFormats!);
    }

    private GetTableSliceHandler Handler(IConditionalFormatStore? formats)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        return new GetTableSliceHandler(
            _rows, _cells, _metadata, units, _access, methodologies, Substitute.For<IPeriodStore>(), styles,
            conditionalFormats: formats);
    }

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static void SetId<T>(T entity, int id)
        where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);
}
