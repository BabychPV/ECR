using Ecr.Application.Documents;
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
/// Сторож шляху читання (P2, AN-125): текстовий результат Formula/Calculated-комірки
/// (<c>ValueString</c> без <c>ValueNumeric</c>) доходить у <c>RowDto.Cells</c> рядком.
/// Розгортання одне на всі шляхи — <c>CellValueMapping.ToRuleValue</c>.
/// </summary>
/// <remarks>
/// Мутація: прибрати гілку <c>ValueString</c> у <c>CellValueMapping.ToRuleValue</c> —
/// комірка стає <c>null</c>, обидва тести червоні.
/// </remarks>
public sealed class GetTableSliceFormulaTextTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int FormulaId = 21;
    private const int CalculatedId = 22;
    private const long Row1 = 1001;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();

    public GetTableSliceFormulaTextTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var formula = new ColumnDef(3, EcrCode.Create("Measure"), Text("Measure"), 1, CellDataType.Formula);
        SetId(formula, FormulaId);
        var calculated = new ColumnDef(3, EcrCode.Create("Result"), Text("Result"), 2, CellDataType.Calculated);
        SetId(calculated, CalculatedId);
        table.AddColumn(formula);
        table.AddColumn(calculated);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [FormulaId] = formula, [CalculatedId] = calculated },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = Row1 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool> { [Row1] = false });
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
        _access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ => ReadScopes.Everything(await _metadata.GetAsync(2, CancellationToken.None)));
        _methodologies.GetMethodologyIdsBoundToTableAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                      .Returns(new List<int>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });

    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Document.View" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    private GetTableSliceHandler Handler()
    {
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);
        var styles = Substitute.For<IStyleCatalog>();
        styles.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new Dictionary<int, StyleDef>());

        return new(_rows, _cells, _metadata, units, _access, _methodologies, _periods, styles);
    }

    private static CellRecord Cell(int columnId, CellValueData value)
        => new(new CellAddress(new PeriodKey(Period), Row1, columnId), TableDefId: 3, value);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Текст_Formula_комірки_доходить_у_зріз_рядком_а_число_числом()
    {
        _cells.ReadSliceAsync(TableInstance, new PeriodKey(Period), Arg.Any<CancellationToken>())
            .Returns([
                Cell(FormulaId, new CellValueData { ValueString = "No - Нет", ValueNumeric = null }),
                Cell(CalculatedId, new CellValueData { ValueNumeric = 12.5m }),
            ]);

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var row = Assert.Single(slice.Rows);
        Assert.Equal("No - Нет", row.Cells["Measure"]);
        Assert.Equal(12.5m, row.Cells["Result"]);
    }

    /// <summary>Фіксує поточну межу: збережений текст Calculated-комірки теж віддається як є.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Текст_Calculated_комірки_у_сховищі_віддається_як_є()
    {
        _cells.ReadSliceAsync(TableInstance, new PeriodKey(Period), Arg.Any<CancellationToken>())
            .Returns([Cell(CalculatedId, new CellValueData { ValueString = "No - Нет" })]);

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal("No - Нет", Assert.Single(slice.Rows).Cells["Result"]);
    }
}
