using Ecr.Application.Common;
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

/// <summary>
/// Метадані груп заголовків колонок: зріз таблиці віддає лише групи з видимими колонками, структура шаблону —
/// усе дерево з підписами всіма мовами; порожній список, коли груп немає.
/// </summary>
public sealed class HeaderGroupMetadataTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IStyleCatalog _styles = Substitute.For<IStyleCatalog>();
    private readonly SheetDef _sheet;
    private readonly TemplateVersionSnapshot _snapshot;

    public HeaderGroupMetadataTests()
    {
        _sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        SetId(_sheet, 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);

        var q1 = new ColumnHeaderGroupDef(3, EcrCode.Create("Q1"), Text("Quarter 1"), 1);
        SetId(q1, 31);
        var jan = new ColumnHeaderGroupDef(3, EcrCode.Create("JAN"), Text("January"), 1, 31);
        SetId(jan, 32);
        var feb = new ColumnHeaderGroupDef(3, EcrCode.Create("FEB"), Text("February"), 2, 31);
        SetId(feb, 33);
        table.AddHeaderGroup(q1);
        table.AddHeaderGroup(jan);
        table.AddHeaderGroup(feb);

        var columns = new Dictionary<int, ColumnDef>();
        foreach (var (code, id, group, hidden) in new[]
                 {
                     ("T", 11, (int?)32, false), ("MG", 12, 32, false),
                     ("T2", 13, 33, true), ("Plain", 14, null, false),
                 })
        {
            var column = new ColumnDef(3, EcrCode.Create(code), Text(code), id, CellDataType.Decimal);
            SetId(column, id);
            column.SetHeaderGroup(group);
            column.SetHidden(hidden);
            table.AddColumn(column);
            columns[id] = column;
        }

        _sheet.AddTable(table);
        _snapshot = new TemplateVersionSnapshot(2, 0, [_sheet], columns, new Dictionary<(int, string), RowDef>());

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(_snapshot);
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long>());
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string>());
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool>());
        _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns([]);
        _cells.ReadSliceAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns([]);
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.Ordinal)));
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new List<int>());
        _styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, StyleDef>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T entity, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = 9,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View", "Template.View" },
        Grants = new Dictionary<string, GrantLevel>(StringComparer.Ordinal),
        Denies = new HashSet<string>(StringComparer.Ordinal),
        RoleIds = new HashSet<int>(),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Зріз_віддає_групи_лише_з_видимими_колонками_і_групу_колонки()
    {
        var handler = new GetTableSliceHandler(_rows, _cells, _metadata, _units, _access, _methodologies, _periods, _styles);

        var slice = await handler.HandleAsync(700, TableInstance, Profile(), "ru", CancellationToken.None);

        // FEB має лише приховану колонку - не віддається; Q1 лишається, бо в піддереві є видима.
        Assert.Equal(["JAN", "Q1"], slice.HeaderGroups.Select(g => g.Code).Order().ToArray());
        var jan = Assert.Single(slice.HeaderGroups, g => g.Code == "JAN");
        Assert.Equal(31, jan.ParentId);
        Assert.Equal("January", jan.Label); // ru немає - ті самі fallback-правила, що й у заголовка колонки
        Assert.Null(Assert.Single(slice.HeaderGroups, g => g.Code == "Q1").ParentId);
        Assert.Equal(3, slice.MaxHeaderGroupDepth);

        Assert.Equal(32, Assert.Single(slice.Columns, c => c.Code == "T").HeaderGroupId);
        Assert.Null(Assert.Single(slice.Columns, c => c.Code == "Plain").HeaderGroupId);
        Assert.DoesNotContain(slice.Columns, c => c.Code == "T2");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Структура_шаблону_віддає_все_дерево_з_усіма_мовами_і_межу_глибини()
    {
        var versions = Substitute.For<IRepository<TemplateVersion, int>>();
        versions.FindAsync(2, Arg.Any<CancellationToken>()).Returns(new TemplateVersion(1, "1.0.0.0", 1, DateTime.UtcNow));
        var documents = Substitute.For<IDocumentStore>();
        documents.GetGroupRulesAsync(2, Arg.Any<CancellationToken>()).Returns([]);
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        var dto = await new GetTemplateStructureHandler(_metadata, _units, versions, documents, access, user)
            .HandleAsync(2, CancellationToken.None);

        var table = dto.Sheets.Single().Tables.Single();
        Assert.Equal(3, dto.MaxHeaderGroupDepth);
        Assert.Equal(3, table.HeaderGroups.Count);
        Assert.Equal("Quarter 1", table.HeaderGroups.Single(g => g.Code == "Q1").LabelL10n.Get("en"));
        Assert.Equal(31, table.HeaderGroups.Single(g => g.Code == "FEB").ParentId);
        Assert.Equal(32, table.Columns.Single(c => c.Code == "MG").HeaderGroupId);
        Assert.Null(table.Columns.Single(c => c.Code == "Plain").HeaderGroupId);
    }
}
