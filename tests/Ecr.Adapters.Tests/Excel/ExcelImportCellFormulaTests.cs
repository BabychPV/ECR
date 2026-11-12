// tests/Ecr.Adapters.Tests/Excel/ExcelImportCellFormulaTests.cs
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// C-4 (аудит 2026-11): формула КОМІРКИ/РЯДКА у вхідній колонці (підсумковий рядок <c>TOTAL</c>) — те, що поклав
/// експорт, а не значення людини. Excel перераховує її після правки входу, і перегляд не має відхиляти через неї книгу.
/// </summary>
/// <remarks>
/// Мутація (червоніє): у <c>ImportDiffBuilder.Build</c> повернути пропуск формули лише за <c>IsCalculated(definition)</c> —
/// <c>TOTAL</c> стає відмовою <c>deny.CalculatedCell</c>, і «усе або нічого» відхиляє всю книгу.
/// </remarks>
public sealed class ExcelImportCellFormulaTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    private static readonly (string Key, long Id, decimal Stored)[] Rows =
    [
        ("R1", 1001, 10m),
        ("R2", 1002, 20m),
        ("R3", 1003, 30m),
        ("TOTAL", 1004, 60m),
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C-4")]
    public void Змінений_вхід_не_відхиляє_книгу_через_формулу_комірки()
    {
        var (table, volume) = Table(FormulaScope.Cell, allowOverride: false);

        using var workbook = new XLWorkbook();
        var worksheet = Sheet(workbook);

        // Людина змінила R1 10 → 20; Excel перерахував TOTAL 60 → 70.
        worksheet.Cell(2, 1).Value = 20;

        var diff = Build(worksheet, table, volume);

        Assert.Empty(diff.Rejected);
        var change = Assert.Single(diff.Changes);
        Assert.Equal("R1", change.RowKey);
        Assert.Equal(20m, change.NewValue);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C-4")]
    public void Число_вписане_замість_формули_комірки_лишається_відмовою()
    {
        // ⚠ Межа: людина стерла формулу й вписала число — це правка обчислюваної комірки, відмова правильна.
        var (table, volume) = Table(FormulaScope.Cell, allowOverride: false);

        using var workbook = new XLWorkbook();
        var worksheet = Sheet(workbook);
        worksheet.Cell(5, 1).Value = 99;

        var diff = Build(worksheet, table, volume);

        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal("TOTAL", rejection.RowKey);
    }

    private static IXLWorksheet Sheet(XLWorkbook workbook)
    {
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(1, 1).Value = "Volume";

        for (var i = 0; i < 3; i++)
        {
            worksheet.Cell(i + 2, 1).Value = Rows[i].Stored;
        }

        // Експорт поклав формулу комірки в TOTAL (`ExcelExporter.WriteFormulas`).
        worksheet.Cell(5, 1).FormulaA1 = "SUM(A2:A4)";
        return worksheet;
    }

    private static (TableDef Table, ColumnDef Volume) Table(FormulaScope scope, bool allowOverride)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var volume = builder.Column(table, "Volume", CellDataType.Decimal);
        RowDef? total = null;
        for (var i = 0; i < Rows.Length; i++)
        {
            var row = builder.Row(table, Rows[i].Key, i + 1);
            total = row;
        }

        var formula = builder.Formula(table, "SUM(Volume)", scope, column: volume, row: total);
        formula.SetAllowManualOverride(allowOverride);
        return (table, volume);
    }

    private static TableDiff Build(IXLWorksheet worksheet, TableDef table, ColumnDef volume)
    {
        var decisions = new Dictionary<CellAddress, EditDecision>();
        var existing = new List<CellRecord>();
        foreach (var (key, id, stored) in Rows)
        {
            var address = new CellAddress(Period, id, volume.Id);
            decisions[address] = key == "TOTAL"
                ? EditDecision.Deny(EditDenyReason.CalculatedCell)
                : EditDecision.Allow();
            existing.Add(new CellRecord(address, table.Id, new CellValueData { ValueNumeric = stored }));
        }

        IReadOnlyList<ExcelColumnRef> columns = [new ExcelColumnRef(volume.Id, "Volume", 1, IsCalculated: false, null)];
        var block = new ExcelTableBlock(
            TableInstance, TableDefId: table.Id, "T0", "S0", HeaderRow: 1,
            Columns: columns,
            Rows: [.. Rows.Select((r, i) => new ExcelRowRef(r.Key, i + 2))]);

        return new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table,
            decisions, new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            Rows.ToDictionary(r => r.Key, r => r.Id, StringComparer.Ordinal),
            Rows.ToDictionary(r => r.Key, _ => "0x0A", StringComparer.Ordinal),
            existing);
    }
}
