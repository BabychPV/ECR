// tests/Ecr.Application.Tests/Documents/VersionMigrationMonthColumnsTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ LAND-YEAR L3: планувальник переносу «копія на місяць» → «рік, місяці в колонках». Немісячна колонка <c>F</c> без
/// відповідника за кодом переходить у <c>F_M01..F_M12</c> (або в колонки псевдонімів <c>F</c>) — перенос, не втрата;
/// селектор, чиє ім'я лишилось лише псевдонімом на НАЯВНІ колонки, — «перерахується»; неповний набір — втрата з переліком.
/// </summary>
/// <remarks>
/// Мутаційні докази: у <c>TryMapMonthColumns</c> <c>found.Count == 12</c> → <c>found.Count &gt;= 11</c> — червоніє
/// «11 місяців»; прибрати <c>!sourceColumnCodes.Contains(candidate.Code)</c> — червоніє «ціль уже є у вихідній».
/// </remarks>
public sealed class VersionMigrationMonthColumnsTests
{
    private const int SourceF = 101;
    private const int SourceS = 102;
    private const int SourceX1 = 103;

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void F_переходить_у_12_місячних_колонок_за_кодом_це_перенос_а_не_втрата()
    {
        var plan = Plan(Source(), Target(months: 12), [new VersionMigrationCellUsage(SourceF, null, 12)], monthColumns: true);

        Assert.Equal(Enumerable.Range(1, 12).ToList(), plan.MonthColumns.Select(c => c.MonthNumber).ToList());
        Assert.All(plan.MonthColumns, c => Assert.Equal(SourceF, c.SourceColumnDefId));
        Assert.Equal(Enumerable.Range(1, 12).Select(m => 2000 + m).ToList(), plan.MonthColumns.Select(c => c.TargetColumnDefId).ToList());
        Assert.Equal((12L, 12L, 0L), (plan.TransferredValues, plan.MonthTransferredValues, plan.LostValues));
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
        var item = Assert.Single(plan.Items, i => i.Kind == VersionMigrationPlanner.MonthColumnsKind);
        Assert.Equal(("S.T.F", 12L, "F_M01..F_M12"), (item.Path, item.Values, item.NewValue));
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Без_прапора_переходу_на_рік_F_втрачається_як_і_раніше()
    {
        var plan = Plan(Source(), Target(months: 12), [new VersionMigrationCellUsage(SourceF, null, 12)], monthColumns: false);

        Assert.Empty(plan.MonthColumns);
        Assert.Equal(12, plan.LostValues);
        Assert.Contains("dataLoss", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Одинадцять_місяців_це_не_мапа_втрата_з_переліком()
    {
        var plan = Plan(Source(), Target(months: 11), [new VersionMigrationCellUsage(SourceF, null, 12)], monthColumns: true);

        Assert.Empty(plan.MonthColumns);
        Assert.Equal(12, plan.LostValues);
        Assert.Equal(["S.T.F"], plan.MonthColumnsUnmapped);
        Assert.Contains("dataLoss", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Порожня_колонка_без_цілі_не_потрапляє_в_перелік()
    {
        var plan = Plan(Source(), Target(months: 11), [], monthColumns: true);

        Assert.Empty(plan.MonthColumnsUnmapped);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Псевдоніми_F_на_нові_місячні_колонки_теж_мапа()
    {
        // Коди цілей інші (Q_1..Q_12), але псевдонім F місяця m веде в Q_m.
        var target = Target(months: 12, code: m => $"Q_{m}", aliasF: true);
        var plan = Plan(Source(), target, [new VersionMigrationCellUsage(SourceF, null, 3)], monthColumns: true);

        Assert.Equal(12, plan.MonthColumns.Count);
        Assert.Equal(3, plan.MonthTransferredValues);
        Assert.Equal(0, plan.LostValues);
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Селектор_з_псевдонімом_на_наявні_колонки_перерахується_не_втрата()
    {
        var plan = Plan(Source(), Target(months: 12, aliasS: true), [new VersionMigrationCellUsage(SourceS, null, 5)], monthColumns: true);

        Assert.DoesNotContain(plan.MonthColumns, c => c.SourceColumnDefId == SourceS);
        Assert.Equal((5L, 0L, 0L), (plan.RecomputedValues, plan.LostValues, plan.TransferredValues));
        Assert.Contains(plan.Items, i => i.Kind == VersionMigrationPlanner.RecomputedKind && i.Path == "S.T.S");
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Task", "LAND-YEAR-L3")]
    public void Ціль_що_вже_є_у_вихідній_таблиці_не_приймає_місячних_значень()
    {
        // F_M05 уже є у вихідній таблиці (переноситься 1:1) — злити на неї значення F травня не можна.
        var plan = Plan(Source(extraCode: "F_M05"), Target(months: 12), [new VersionMigrationCellUsage(SourceF, null, 12)], monthColumns: true);

        Assert.DoesNotContain(plan.MonthColumns, c => c.SourceColumnDefId == SourceF);
        Assert.Equal(12, plan.LostValues);
    }

    private static VersionMigrationPlan Plan(
        TemplateVersionSnapshot source, TemplateVersionSnapshot target, IReadOnlyList<VersionMigrationCellUsage> values, bool monthColumns)
        => VersionMigrationPlanner.Plan(source, target, values, new Dictionary<int, long>(), monthColumns: monthColumns);

    /// <summary>Вихідна: S → T (місяці в колонках): X1 (місячний ввід 1), F (немісячна формула), S (селектор).</summary>
    private static TemplateVersionSnapshot Source(string? extraCode = null)
    {
        var (sheet, table) = Shell(1);
        Add(table, SourceX1, "X1", CellDataType.Decimal, month: 1);
        Add(table, SourceF, "F", CellDataType.Formula, month: null);
        Add(table, SourceS, "S", CellDataType.Formula, month: null);
        if (extraCode is not null)
        {
            Add(table, 104, extraCode, CellDataType.Formula, month: 5);
        }

        return Snapshot(1, sheet);
    }

    private static TemplateVersionSnapshot Target(int months, Func<int, string>? code = null, bool aliasF = false, bool aliasS = false)
    {
        var (sheet, table) = Shell(2);
        Add(table, 1001, "X1", CellDataType.Decimal, month: 1);
        var aliases = new Dictionary<(int, int), IReadOnlyList<MonthInputAlias>>();
        for (var m = 1; m <= months; m++)
        {
            var id = 2000 + m;
            Add(table, id, code?.Invoke(m) ?? $"F_M{m:00}", CellDataType.Formula, month: (byte)m);
            var list = new List<MonthInputAlias>();
            if (aliasF)
            {
                list.Add(new MonthInputAlias(table.Id, "F", (byte)m, id));
            }

            if (aliasS)
            {
                list.Add(new MonthInputAlias(table.Id, "S", (byte)m, 1001));   // селектор: наявний місячний ввід
            }

            aliases[(table.Id, m)] = list;
        }

        return Snapshot(2, sheet) with { MonthInputAliases = aliases };
    }

    private static (SheetDef Sheet, TableDef Table) Shell(int versionId)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create("S"), Name("S"), 1);
        SetId(sheet, versionId * 10);
        var table = new TableDef(1, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        SetId(table, versionId * 100);
        sheet.AddTable(table);
        return (sheet, table);
    }

    private static void Add(TableDef table, int id, string code, CellDataType type, byte? month)
    {
        var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), id, type);
        SetId(column, id);
        if (month is { } m)
        {
            column.SetMonth(true, m);
        }

        table.AddColumn(column);
    }

    private static TemplateVersionSnapshot Snapshot(int versionId, SheetDef sheet)
        => new(versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(), RowsByKey: new Dictionary<(int, string), RowDef>());

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
