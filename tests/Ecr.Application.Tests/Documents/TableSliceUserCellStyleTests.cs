using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// CELL-STYLE L3b: зріз віддає користувацьке оформлення комірок за ключем
/// <c>"{rowKey}:{columnCode}"</c> — лише для видимих читачеві колонок (нерозкриття, S6).
/// </summary>
public sealed class TableSliceUserCellStyleTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const int HiddenId = 12;
    private static readonly DateTime At = new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICellStyleStore _styles = Substitute.For<ICellStyleStore>();

    public TableSliceUserCellStyleTests()
    {
        var volume = new ColumnDef(TableDef, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);
        var hidden = new ColumnDef(TableDef, EcrCode.Create("Secret"), Text("Secret"), 2, CellDataType.Decimal);
        SetId(hidden, HiddenId);
        hidden.SetHidden(true);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(volume);
        table.AddColumn(hidden);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, TableDef, 2, Period));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>());

        var snapshot = new TemplateVersionSnapshot(2, 0, [sheet],
            new Dictionary<int, ColumnDef> { [VolumeId] = volume, [HiddenId] = hidden },
            new Dictionary<(int, string), RowDef>());
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(snapshot);

        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
        // Колонку Secret читач не бачить (заборона на колонку, S6).
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(_ => ReadScopes.Everything(snapshot));

        _styles.ReadInstanceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<CellStyle>>(
               [
                   Styled(1, VolumeId, "#FFFF00", "#FF0000", true),
                   Styled(2, VolumeId, null, "#0000FF", false),
                   Styled(2, HiddenId, "#00FF00", null, false),
                   Styled(999, VolumeId, "#FFFFFF", null, false),
               ]));
    }

    [Fact]
    public async Task Slice_returns_user_styles_per_cell_without_orphans()
    {
        var slice = await Handler(_styles).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(slice.CellStyles!.ContainsKey("R1:Volume"));
        Assert.Equal(new CellFormatDto("#FFFF00", "#FF0000", true), slice.CellStyles["R1:Volume"]);
        Assert.Equal(new CellFormatDto(null, "#0000FF", false), slice.CellStyles["R2:Volume"]);
        Assert.False(slice.CellStyles.ContainsKey("R2:Secret"));

        // Сирота (рядка 999 немає в зрізі) не потрапляє у відповідь.
        Assert.DoesNotContain(slice.CellStyles.Keys, k => k.StartsWith("999", StringComparison.Ordinal));
        await _styles.Received(1).ReadInstanceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Slice_without_store_has_empty_user_styles()
    {
        var slice = await Handler(null).HandleAsync(Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Empty(slice.CellStyles!);
    }

    private GetTableSliceHandler Handler(ICellStyleStore? styles)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var catalog = Substitute.For<IStyleCatalog>();
        catalog.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        return new GetTableSliceHandler(
            _rows, _cells, _metadata, units, _access, methodologies, Substitute.For<IPeriodStore>(), catalog,
            cellStyles: styles);
    }

    private static CellStyle Styled(long rowId, int columnId, string? background, string? foreground, bool bold)
    {
        var style = new CellStyle(new CellAddress(new PeriodKey(Period), rowId, columnId), 1, At);
        style.Apply(background, foreground, bold, 1, At);
        return style;
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
