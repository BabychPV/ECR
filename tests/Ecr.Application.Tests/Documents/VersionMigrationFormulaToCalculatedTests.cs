// tests/Ecr.Application.Tests/Documents/VersionMigrationFormulaToCalculatedTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// ✎ P-1124-2 (вода T7b_00, Land 1.0.21.0): колонка, яку в цільовій версії рахує МЕТОДОЛОГІЯ замість формули таблиці
/// (Formula -> Calculated). Її комірки — обчислені значення формули, не введення людини: перенос їх не несе (методологія
/// відтворить результат), рахує «перераховано», а не «втрачено» і не «змінене тлумачення» (guardedWithData).
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати гілку Formula -> Calculated у <c>CompareColumns</c> — червоніє перший тест (guardedWithData,
/// значення в мапі колонок); у циклі комірок прибрати <c>derived.Contains</c> — «втрачено 4» замість «перераховано 4».
/// </remarks>
public sealed class VersionMigrationFormulaToCalculatedTests
{
    private const int SourceW = 301;
    private const int SourceX = 302;
    private const int TargetW = 401;
    private const int TargetX = 402;

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public void Формула_таблиці_що_стала_виходом_методології_перераховується_а_не_переноситься_й_не_блокує()
    {
        var plan = Plan(CellDataType.Formula, CellDataType.Calculated,
            [new VersionMigrationCellUsage(SourceW, null, 4), new VersionMigrationCellUsage(SourceX, null, 3)]);

        Assert.DoesNotContain(plan.Columns, c => c.SourceColumnDefId == SourceW);
        Assert.Contains(plan.Columns, c => c.SourceColumnDefId == SourceX && c.TargetColumnDefId == TargetX);
        Assert.Equal((4L, 0L, 3L, 0L), (plan.RecomputedValues, plan.LostValues, plan.TransferredValues, plan.GuardedValues));
        var item = Assert.Single(plan.Items, i => i.Path == "S.T.W");
        Assert.Equal((VersionMigrationPlanner.RecomputedKind, ChangeClass.Guarded, "Formula", "Calculated"),
            (item.Kind, item.ChangeClass, item.OldValue, item.NewValue));
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "P-1124-2")]
    public void Введена_колонка_що_стала_обчислюваною_і_далі_змінене_тлумачення()
    {
        // Звичайне введення (Decimal) -> Calculated: значення людини, не похідне — як і раніше guardedWithData.
        var plan = Plan(CellDataType.Decimal, CellDataType.Calculated, [new VersionMigrationCellUsage(SourceW, null, 4)]);

        Assert.Equal((0L, 4L), (plan.RecomputedValues, plan.GuardedValues));
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    private static VersionMigrationPlan Plan(CellDataType from, CellDataType to, IReadOnlyList<VersionMigrationCellUsage> values)
        => VersionMigrationPlanner.Plan(Snapshot(1, SourceW, SourceX, from), Snapshot(2, TargetW, TargetX, to), values, new Dictionary<int, long>());

    private static TemplateVersionSnapshot Snapshot(int versionId, int w, int x, CellDataType wType)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create("S"), Name("S"), 1);
        SetId(sheet, versionId * 10);
        var table = new TableDef(1, EcrCode.Create("T"), Name("T"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, versionId * 100);
        sheet.AddTable(table);
        Add(table, w, "W", wType);
        Add(table, x, "X", CellDataType.Decimal);
        return new(versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(), RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void Add(TableDef table, int id, string code, CellDataType type)
    {
        var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), id, type);
        SetId(column, id);
        table.AddColumn(column);
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
