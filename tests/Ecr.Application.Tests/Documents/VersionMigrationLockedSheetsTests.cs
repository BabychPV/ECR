// tests/Ecr.Application.Tests/Documents/VersionMigrationLockedSheetsTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// LP-32: які аркуші вихідної версії план «зачіпає» (<c>VersionMigrationPlan.TouchedSheets</c>) — лише вони блокують
/// перенос, коли на них є подане/затверджене. Додане, презентація — не зачіпають.
/// </summary>
/// <remarks>
/// ⚠ Мутаційні докази (червоніють названі тести): у <c>VersionMigrationPlanner.TouchesLockedSheet</c> прибрати
/// <c>Renamed</c> — «Перейменування»; замінити <c>ChangeClass.Guarded</c> на <c>Breaking</c> — «Прибрана колонка без
/// значень»; прибрати <c>newRows.Count &gt; newRowsBefore</c> — «Новий рядок»; прибрати <c>touched.Add</c> у гілці
/// відсутнього аркуша — «Прибраний аркуш».
/// </remarks>
public sealed class VersionMigrationLockedSheetsTests
{
    private const int S1Source = 110;
    private const int S2Source = 120;

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Додана_колонка_і_нова_таблиця_не_зачіпають_аркуш()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S1", ["A", "B"], ["R1"], extraTable: "T_NEW"), Sheet("S2", ["A"], ["R1"])]);

        Assert.Contains(plan.Items, i => i.Kind == "Added");
        Assert.Empty(plan.TouchedSheets);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Лише_підпис_аркуша_не_зачіпає_його()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"])],
            [Sheet("S1", ["A"], ["R1"], name: "Інша назва")]);

        Assert.Contains(plan.Items, i => i.Kind == "Presentation");
        Assert.Empty(plan.TouchedSheets);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Прибрана_колонка_без_значень_зачіпає_лише_свій_аркуш()
    {
        var plan = Plan(
            [Sheet("S1", ["A", "B"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])]);

        Assert.Contains(plan.Items, i => i.Kind == "Removed");
        Assert.Equal([S1Source], plan.TouchedSheets.Order());
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Прибрана_колонка_зі_значеннями_зачіпає_аркуш()
    {
        var plan = Plan(
            [Sheet("S1", ["A", "B"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            cells: [new VersionMigrationCellUsage(S1Source * 100 + 2, null, 5)]);

        Assert.Contains(plan.Items, i => i.Kind == "Lost");
        Assert.Equal([S1Source], plan.TouchedSheets.Order());
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Новий_рядок_фіксованої_таблиці_зачіпає_аркуш()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1", "R2"])]);

        Assert.Single(plan.NewRows);
        Assert.Equal([S2Source], plan.TouchedSheets.Order());
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Прибраний_аркуш_зачіпається()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S2", ["A"], ["R1"])]);

        Assert.Equal([S1Source], plan.TouchedSheets.Order());
        Assert.DoesNotContain(S1Source, plan.Sheets.Keys);
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Перейменування_таблиці_за_мапою_зачіпає_аркуш_хоч_дані_не_губляться()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"], tableCode: "T_OLD")],
            [Sheet("S1", ["A"], ["R1"], tableCode: "T_NEW")],
            cells: [new VersionMigrationCellUsage(S1Source * 100 + 1, null, 3)],
            renames: [new VersionMigrationTableRename("T_OLD", "T_NEW")]);

        Assert.Equal(0, plan.LostValues);
        Assert.Contains(plan.Items, i => i.Kind == "Renamed");
        Assert.Equal([S1Source], plan.TouchedSheets.Order());
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Зміна_типу_колонки_зачіпає_аркуш()
    {
        var plan = Plan(
            [Sheet("S1", ["A"], ["R1"]), Sheet("S2", ["A"], ["R1"])],
            [Sheet("S1", ["A"], ["R1"], dataType: CellDataType.String), Sheet("S2", ["A"], ["R1"])]);

        Assert.Contains(plan.Items, i => i.Kind == "Modified" && i.Field == "DataType");
        Assert.Equal([S1Source], plan.TouchedSheets.Order());
    }

    private static VersionMigrationPlan Plan(
        IReadOnlyList<SheetSpec> from, IReadOnlyList<SheetSpec> to,
        IReadOnlyList<VersionMigrationCellUsage>? cells = null,
        IReadOnlyList<VersionMigrationTableRename>? renames = null)
        => VersionMigrationPlanner.Plan(
            Version(1, from), Version(2, to), cells ?? [], new Dictionary<int, long>(), renames);

    private sealed record SheetSpec(
        string Code, IReadOnlyList<string> Columns, IReadOnlyList<string> Rows,
        string TableCode, string? ExtraTable, string Name, CellDataType DataType);

    private static SheetSpec Sheet(
        string code, string[] columns, string[] rows, string tableCode = "T",
        string? extraTable = null, string name = "Sheet", CellDataType dataType = CellDataType.Decimal)
        => new(code, columns, rows, tableCode, extraTable, name, dataType);

    // Ідентифікатори: аркуш — (версія 1: 110/120; версія 2: 210/220), таблиця = аркуш*10, колонка = аркуш*100 + №.
    private static TemplateVersionSnapshot Version(int versionId, IReadOnlyList<SheetSpec> specs)
    {
        var sheets = new List<SheetDef>();
        foreach (var spec in specs)
        {
            var order = spec.Code == "S1" ? 1 : 2;
            var sheetId = (versionId * 100) + (order * 10);
            var sheet = new SheetDef(versionId, EcrCode.Create(spec.Code), Name(spec.Name), order);
            SetId(sheet, sheetId);

            var tableId = sheetId * 10;
            var table = new TableDef(
                sheetId, EcrCode.Create(spec.TableCode), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            SetId(table, tableId);

            for (var i = 0; i < spec.Columns.Count; i++)
            {
                var column = new ColumnDef(
                    tableId, EcrCode.Create(spec.Columns[i]), Name(spec.Columns[i]), i + 1, spec.DataType);
                SetId(column, (sheetId * 100) + i + 1);
                table.AddColumn(column);
            }

            for (var i = 0; i < spec.Rows.Count; i++)
            {
                var row = new RowDef(tableId, RowKey.Create(spec.Rows[i]), i + 1, Name(spec.Rows[i]), RowKind.Item);
                SetId(row, (tableId * 100) + i + 1);
                table.AddRow(row);
            }

            sheet.AddTable(table);

            if (spec.ExtraTable is not null)
            {
                var extra = new TableDef(
                    sheetId, EcrCode.Create(spec.ExtraTable), Name("X"), 2, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                SetId(extra, (sheetId * 10) + 1);
                sheet.AddTable(extra);
            }

            sheets.Add(sheet);
        }

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: sheets,
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
