// tests/Ecr.Application.Tests/Documents/VersionMigrationResumeTests.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Б1 (2026-10-21): перенос комірок йде діапазонами з окремими комітами, тож повторний запуск після обриву бачить
/// частину значень уже на колонках ЦІЛЬОВОЇ версії. Планувальник мусить зарахувати їх як перенесені, а не як втрачені.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати з <c>VersionMigrationPlanner.Plan</c> відфільтровування комірок на колонках цільової версії
/// (<c>alreadyMoved</c>) — «втрачено» стає 3, режим Safe відмовляє, повтор після обриву неможливий.
/// </remarks>
public sealed class VersionMigrationResumeTests
{
    private const int SourceColumn = 101;
    private const int TargetColumn = 201;

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Значення_вже_перенесені_на_цільову_колонку_не_є_втратою_і_рахуються_перенесеними()
    {
        var plan = Plan(new VersionMigrationCellUsage(SourceColumn, null, 5), new VersionMigrationCellUsage(TargetColumn, null, 3));

        Assert.Equal(0, plan.LostValues);
        Assert.Equal(8, plan.TransferredValues);
        Assert.Empty(plan.RefusalsFor(VersionMigrationMode.Safe));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Звіт_плану_не_залежить_від_того_скільки_вже_перенесено()
    {
        var fresh = Plan(new VersionMigrationCellUsage(SourceColumn, null, 8));
        var resumed = Plan(new VersionMigrationCellUsage(SourceColumn, null, 2), new VersionMigrationCellUsage(TargetColumn, null, 6));

        Assert.Equal(fresh.TransferredValues, resumed.TransferredValues);
        Assert.Equal(fresh.LostValues, resumed.LostValues);
        Assert.Equal(fresh.Columns.Select(c => (c.SourceColumnDefId, c.TargetColumnDefId)), resumed.Columns.Select(c => (c.SourceColumnDefId, c.TargetColumnDefId)));
    }

    [Fact]
    [Trait("Requirement", "ФВ-7.5")]
    public void Чужа_колонка_що_не_належить_жодній_із_версій_лишається_втратою()
    {
        var plan = Plan(new VersionMigrationCellUsage(SourceColumn, null, 5), new VersionMigrationCellUsage(999, null, 4));

        Assert.Equal(4, plan.LostValues);
        Assert.Equal(5, plan.TransferredValues);
    }

    private static VersionMigrationPlan Plan(params VersionMigrationCellUsage[] cells)
        => VersionMigrationPlanner.Plan(
            Version(1, 100, SourceColumn), Version(2, 200, TargetColumn), cells, new Dictionary<int, long>());

    private static TemplateVersionSnapshot Version(int versionId, int tableId, int columnId)
    {
        var sheet = new SheetDef(versionId, EcrCode.Create("S54"), Name("Sheet"), 1);
        SetId(sheet, versionId * 10);

        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("T54"), Name("T54"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, tableId);
        var column = new ColumnDef(tableId, EcrCode.Create("A"), Name("A"), 1, CellDataType.Decimal);
        SetId(column, columnId);
        table.AddColumn(column);
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
