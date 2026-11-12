// tests/Ecr.Application.Tests/Documents/VersionMigrationMonthPlacementTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// V-6 (аудит 2026-11, 09-version-lifecycle): місце колонки в році (<c>IsMonthColumn</c>/<c>MonthNumber</c>)
/// визначає період-власника введеного значення (Q-17, <c>MonthColumnRouter.OwnerOf</c>) і місяць у стабільних
/// вʼюхах. Перенос періоду екземпляра не міняє, тож інше місце колонки = значення в іншому місяці: зміна
/// тлумачення, <c>Modified</c>/Guarded зі значеннями (відмова <c>guardedWithData</c>), як для типу колонки.
/// </summary>
/// <remarks>
/// ⚠ Мутаційний доказ: у <c>CompareColumns</c> прибрати виклик <c>Guarded(… "MonthPlacement" …)</c> — червоніють
/// усі кейси, де місце змінено.
/// </remarks>
public sealed class VersionMigrationMonthPlacementTests
{
    private const int SourceColumn = 101;
    private const int SourceRow = 301;

    [Fact]
    [Trait("Requirement", "V-6")]
    public void Колонка_стала_місячною_зі_значеннями_це_Guarded_і_guardedWithData()
    {
        var plan = Plan(
            source: Version(1, month: null),
            target: Version(2, month: 3),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        var item = Assert.Single(plan.Items, i => i.Field == "MonthPlacement");
        Assert.Equal(("Modified", ChangeClass.Guarded, 5L, "S02.T02.A", "none", "3"),
            (item.Kind, item.ChangeClass, item.Values, item.Path, item.OldValue, item.NewValue));
        Assert.Equal(5, plan.GuardedValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));

        // Це не нова формула (ACC-OW): згода на перезапис похідних значень відмову не знімає.
        Assert.Equal(0, plan.GuardedOverwritableValues);
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe, acceptGuardedOverwrite: true));
    }

    [Fact]
    [Trait("Requirement", "V-6")]
    public void Інший_місяць_колонки_зі_значеннями_це_Guarded()
    {
        var plan = Plan(
            source: Version(1, month: 3),
            target: Version(2, month: 4),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 2)]);

        var item = Assert.Single(plan.Items, i => i.Field == "MonthPlacement");
        Assert.Equal((ChangeClass.Guarded, "3", "4"), (item.ChangeClass, item.OldValue, item.NewValue));
        Assert.Contains("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "V-6")]
    public void Колонка_перестала_бути_місячною_це_Guarded()
    {
        var plan = Plan(
            source: Version(1, month: 12),
            target: Version(2, month: null),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 1)]);

        var item = Assert.Single(plan.Items, i => i.Field == "MonthPlacement");
        Assert.Equal(("12", "none"), (item.OldValue, item.NewValue));
        Assert.Equal(1, plan.GuardedValues);
    }

    [Fact]
    [Trait("Requirement", "V-6")]
    public void Зміна_місця_без_значень_не_відмовляє()
    {
        var plan = Plan(source: Version(1, month: null), target: Version(2, month: 3), values: []);

        var item = Assert.Single(plan.Items, i => i.Field == "MonthPlacement");
        Assert.Equal(0, item.Values);
        Assert.Equal(0, plan.GuardedValues);
        Assert.DoesNotContain("guardedWithData", plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "V-6")]
    public void Те_саме_місце_колонки_відмінності_не_дає()
    {
        var plan = Plan(
            source: Version(1, month: 3),
            target: Version(2, month: 3),
            values: [new VersionMigrationCellUsage(SourceColumn, SourceRow, 5)]);

        Assert.DoesNotContain(plan.Items, i => i.Field == "MonthPlacement");
        Assert.Equal(0, plan.GuardedValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    private static VersionMigrationPlan Plan(
        TemplateVersionSnapshot source, TemplateVersionSnapshot target, IReadOnlyList<VersionMigrationCellUsage> values)
        => VersionMigrationPlanner.Plan(source, target, values, new Dictionary<int, long>());

    /// <summary>Версія: аркуш S02 → таблиця T02 → колонка A (опційно місячна) і B; рядок R1.</summary>
    private static TemplateVersionSnapshot Version(int versionId, byte? month)
    {
        var baseId = versionId * 100;
        var sheet = new SheetDef(versionId, EcrCode.Create("S02"), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T02"), Name("T02"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, baseId);

        var columnA = new ColumnDef(table.Id, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal);
        SetId(columnA, baseId + 1);
        if (month is { } m)
        {
            columnA.SetMonth(true, m);
        }

        var columnB = new ColumnDef(table.Id, EcrCode.Create("B"), Name("B"), 2, CellDataType.Decimal);
        SetId(columnB, baseId + 2);
        table.AddColumn(columnA);
        table.AddColumn(columnB);

        var row1 = new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item);
        SetId(row1, baseId + 201);
        table.AddRow(row1);

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            versionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
