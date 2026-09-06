// tests/Ecr.Application.Tests/Documents/GetTableSliceTests.cs
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
/// Читання зрізу — бюджет **p95 400 мс** на ~5 000 комірок (tz/08 §8.2).
/// Саме тому права перевіряються одним викликом, а не покомірково.
/// </summary>
public sealed class GetTableSliceTests
{
    private const long TableInstance = 500;
    private const int Period = 202601;
    private const int VolumeId = 11;
    private const long Row1 = 1001;

    private readonly IRowStore _rows = Substitute.For<IRowStore>();
    private readonly ICellStore _cells = Substitute.For<ICellStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();

    public GetTableSliceTests()
    {
        var sheet = new SheetDef(2, EcrCode.Create("Water"), Text("Water"), 1);
        var table = new TableDef(1, EcrCode.Create("Main"), Text("Main"), 1,
                                 TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, 3);
        var column = new ColumnDef(3, EcrCode.Create("Volume"), Text("Volume"), 1, CellDataType.Decimal);
        SetId(column, VolumeId);
        table.AddColumn(column);
        sheet.AddTable(table);

        _rows.ResolveTableInstanceAsync(TableInstance, Arg.Any<CancellationToken>())
             .Returns(new TableInstanceRef(TableInstance, 700, 3, 2, Period));
        _metadata.GetAsync(2, Arg.Any<CancellationToken>()).Returns(
            new TemplateVersionSnapshot(2, 0, [sheet],
                new Dictionary<int, ColumnDef> { [VolumeId] = column },
                new Dictionary<(int, string), RowDef>()));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["7001001"] = Row1 });
        _rows.GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, string> { ["7001001"] = "0x0A" });
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool> { [Row1] = false });
        _access.CanEditSliceAsync(Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>())
               .Returns(new Dictionary<CellAddress, EditDecision>());
    }

    private static LocalizedText Text(string s) => new(new Dictionary<string, string> { ["en"] = s });
    private static void SetId<T>(T e, int id) where T : Entity<int>
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(e, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = 9, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>()
    };

    /// <summary>
    /// Довідник одиниць для зрізу.
    /// </summary>
    /// <remarks>
    /// ⚠ Порожній навмисно: ці тести не про одиниці, і колонка без
    /// одиниці має віддавати `null`, а не падати. Заповнений довідник
    /// тут зробив би фікстуру обізнанішою за перевірку.
    /// </remarks>
    private static IUnitCatalog Units()
    {
        var catalogue = NSubstitute.Substitute.For<IUnitCatalog>();
        catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        return catalogue;
    }

    private GetTableSliceHandler Handler() => new(_rows, _cells, _metadata, Units(), _access);

    private void Cells(params CellRecord[] records)
        => _cells.ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>()).Returns(records);

    private static CellRecord Cell(long rowId, CellValueData value)
        => new(new CellAddress(new PeriodKey(Period), rowId, VolumeId), TableDefId: 3, value);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.8")]
    public async Task Порожні_комірки_не_повертаються()
    {
        // У сховищі є лише одна матеріалізована комірка. Другої не існує —
        // незаповнені комірки не матеріалізуються (ФВ-3.8).
        Cells(Cell(Row1, new CellValueData { ValueNumeric = 12500m }));

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var row = Assert.Single(slice.Rows);
        Assert.Single(row.Cells);
        Assert.Equal(12500m, row.Cells["Volume"]);

        // Колонка в описі є завжди — клієнт бере з неї DefaultValue для тих
        // комірок, яких у зрізі немає.
        Assert.Single(slice.Columns);
        Assert.Equal("Volume", slice.Columns[0].Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-6.10")]
    public async Task Права_перевіряються_одним_викликом_на_зріз()
    {
        Cells(Cell(Row1, new CellValueData { ValueNumeric = 1m }));

        await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        // Рівно один виклик на весь зріз. Поштучна перевірка комірок — це
        // 5000 викликів і гарантований вихід за бюджет 400 мс.
        await _access.Received(1).CanEditSliceAsync(
            Arg.Any<AccessProfile>(), TableInstance, Arg.Any<CancellationToken>());
        await _access.DidNotReceive().CanEditCellAsync(
            Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CellAddress>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Немає_запиту_на_кожен_рядок()
    {
        Cells(
            Cell(1001, new CellValueData { ValueNumeric = 1m }),
            Cell(1002, new CellValueData { ValueNumeric = 2m }),
            Cell(1003, new CellValueData { ValueNumeric = 3m }));
        _rows.GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<string, long> { ["a"] = 1001, ["b"] = 1002, ["c"] = 1003 });

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.Equal(3, slice.Rows.Count);

        // Три рядки — але сховище опитане рівно по одному разу на кожен вид
        // даних. N+1 тут не «неоптимальність», а зруйнований бюджет.
        await _cells.Received(1).ReadSliceAsync(TableInstance, Arg.Any<CancellationToken>());
        await _rows.Received(1).GetRowIdsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _rows.Received(1).GetRowVersionsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
        await _cells.DidNotReceive().ReadCellsAsync(
            Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Явна_порожнеча_повертається_окремою_ознакою()
    {
        Cells(Cell(Row1, CellValueData.Empty));

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        var row = Assert.Single(slice.Rows);

        // Ключ ПРИСУТНІЙ, значення null — це «заповнили порожнім».
        // Якби зріз просто пропускав такі комірки, клієнт підставив би
        // DefaultValue і показав користувачеві не те, що той увів (R-B4).
        Assert.True(row.Cells.ContainsKey("Volume"));
        Assert.Null(row.Cells["Volume"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Ознака_IsOrphaned_читається_а_не_перераховується()
    {
        Cells(Cell(Row1, new CellValueData { ValueNumeric = 1m }));
        _rows.GetOrphanFlagsAsync(TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
             .Returns(new Dictionary<long, bool> { [Row1] = true });

        var slice = await Handler().HandleAsync(700, TableInstance, Profile(), "en", CancellationToken.None);

        Assert.True(Assert.Single(slice.Rows).IsOrphaned);

        // Ознака ВЗЯТА зі збереженого поля одним запитом. Перевіряти чинність
        // записів реєстру на кожен рядок означало б N запитів і вихід за
        // бюджет 400 мс; тому її ставить нічний OrphanScanJob (ФВ-8.13, D-98).
        await _rows.Received(1).GetOrphanFlagsAsync(
            TableInstance, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());

        // Читання осиротілий рядок не блокує — він приходить у зрізі як
        // звичайний, лише з піднятим прапорцем. Блокує Submit.
        Assert.Equal(1m, Assert.Single(slice.Rows).Cells["Volume"]);
    }
}
