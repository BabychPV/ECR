using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// CELL-STYLE L2: зріз віддає стиль шапки таблиці (<c>TableDef.HeaderStyleId</c>) і стиль рядка шаблону
/// (<c>RowDef.StyleId</c>) тим самим <c>CellStyleDto</c>, що й колонка.
/// </summary>
/// <remarks>
/// Мутація: прибрати <c>HeaderStyle:</c> / <c>Style:</c> у <c>GetTableSliceHandler</c> — перший/другий тест червоні;
/// прибрати <c>StyleOf</c> для рядка без <c>RowDef</c> (динамічний) — третій червоний лише через NRE.
/// </remarks>
public sealed class TableSliceHeaderAndRowStyleTests
{
    private const long Document = 700;
    private const long TableInstance = 500;
    private const int TableDef = 3;
    private const int Period = 202601;
    private const int HeaderStyleId = 41;
    private const int RowStyleId = 42;
    private const int VolumeId = 11;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly TemplateVersionSnapshot _snapshot;

    public TableSliceHeaderAndRowStyleTests()
    {
        var volume = new ColumnDef(TableDef, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(volume, VolumeId);

        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, TableDef);
        table.AddColumn(volume);
        table.SetHeaderStyle(HeaderStyleId);

        var styled = new RowDef(TableDef, RowKey.Create("R1"), 0, Text("Styled"), RowKind.Item);
        styled.SetStyle(RowStyleId);
        var plain = new RowDef(TableDef, RowKey.Create("R2"), 1, Text("Plain"), RowKind.Item);
        table.AddRow(styled);
        table.AddRow(plain);
        sheet.AddTable(table);

        _snapshot = new TemplateVersionSnapshot(2, 0, [sheet],
            new Dictionary<int, ColumnDef> { [VolumeId] = volume },
            new Dictionary<(int, string), RowDef>());

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, Document, TableDef, 2, Period));
        // R3 — динамічний рядок: у шаблоні RowDef немає.
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["R1"] = 1, ["R2"] = 2, ["R3"] = 3 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(new List<CellRecord>());

        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(_ => ReadScopes.Everything(_snapshot));
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(EditDecision.Allow());
    }

    [Fact]
    public async Task Slice_returns_header_style_of_table()
    {
        var slice = await Handler(KnownStyles()).HandleAsync(
            Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.NotNull(slice.HeaderStyle);
        Assert.True(slice.HeaderStyle!.IsBold);
        Assert.Equal(unchecked((int)0xFFCCFFCC), slice.HeaderStyle.BackgroundArgb);
    }

    [Fact]
    public async Task Slice_returns_row_style_only_for_styled_template_rows()
    {
        var slice = await Handler(KnownStyles()).HandleAsync(
            Document, TableInstance, Profile(), "en", CancellationToken.None);

        var byKey = slice.Rows.ToDictionary(r => r.RowKey);
        Assert.NotNull(byKey["R1"].Style);
        Assert.Equal(unchecked((int)0xFFFFFF00), byKey["R1"].Style!.BackgroundArgb);
        Assert.Null(byKey["R2"].Style);   // рядок шаблону без стилю
        Assert.Null(byKey["R3"].Style);   // динамічний рядок: RowDef немає
    }

    [Fact]
    public async Task Dangling_style_reference_gives_null_not_error()
    {
        var slice = await Handler(new Dictionary<int, StyleDef>()).HandleAsync(
            Document, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Null(slice.HeaderStyle);
        Assert.All(slice.Rows, r => Assert.Null(r.Style));
    }

    [Fact]
    public async Task Denied_table_discloses_neither_slice_nor_styles()
    {
        // Заборона на таблицю: зріз — 404 до читання стилів, окремого каналу витоку стилю шапки немає.
        var profile = new AccessBuilder { UserId = 9 }
            .Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Read)
            .Deny(ResourceKind.Table, TableDef)
            .Build();
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(_ => DocumentReadScope.For(profile, AccessBuilder.ProjectId, _snapshot));

        await Assert.ThrowsAsync<NotFoundException>(
            () => Handler(KnownStyles()).HandleAsync(
                Document, TableInstance, Profile(), "en", CancellationToken.None));
    }

    private static Dictionary<int, StyleDef> KnownStyles()
    {
        var header = new StyleDef(2, EcrCode.Create("HDR"));
        header.SetAppearance(null, null, true, false, null, unchecked((int)0xFFCCFFCC), null, null, null, true, null);
        SetId(header, HeaderStyleId);

        var row = new StyleDef(2, EcrCode.Create("ROW"));
        row.SetAppearance(null, null, false, false, null, unchecked((int)0xFFFFFF00), null, null, null, false, null);
        SetId(row, RowStyleId);

        return new Dictionary<int, StyleDef> { [HeaderStyleId] = header, [RowStyleId] = row };
    }

    private GetTableSliceHandler Handler(Dictionary<int, StyleDef> known)
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(known);
        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(TableDef, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        return new GetTableSliceHandler(
            _rows, _cells, _metadata, units, _access, methodologies, Substitute.For<IPeriodStore>(), styles);
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
