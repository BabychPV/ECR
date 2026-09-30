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
/// S6 (ФВ-6.6): перегляд імпорту не порівнює книгу з ПРИХОВАНОЮ колонкою —
/// відповідь про неї не залежить від її поточного значення.
/// </summary>
/// <remarks>
/// ⛔ До фіксу порівняння з поточним значенням ішло ДО рішення про доступ:
/// вписане в книгу число, що збігалося з прихованим, давало «нічого не
/// зміниться», а інше — відмову правами. Підставляючи числа, приховане
/// значення читалося без права на читання.
/// </remarks>
public sealed class ImportDiffBuilderHiddenColumnTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const string RowKey = "R1";
    private const long RowId = 1001;
    private const decimal Stored = 4242.5m;

    private static readonly PeriodKey Period = new(PeriodKeyValue);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Прихована_колонка_відповідь_не_залежить_від_її_значення()
    {
        var (table, visible, hidden) = Table();

        var same = Build(table, visible, hidden, hiddenValue: Stored, hide: true);
        var other = Build(table, visible, hidden, hiddenValue: Stored + 1, hide: true);

        foreach (var diff in new[] { same, other })
        {
            Assert.DoesNotContain(diff.Changes, c => c.ColumnCode == hidden.Code);
            var rejection = Assert.Single(diff.Rejected);
            Assert.Equal(hidden.Code, rejection.ColumnCode);
            Assert.Equal("ECR-ACCS-0403", rejection.ReasonCode);
            Assert.Equal("deny.NoGrant", rejection.MessageKey);
        }

        // ⛔ Головне: книга «вгадала» і «не вгадала» — ОДНАКОВИЙ перегляд.
        Assert.Equal(Describe(same), Describe(other));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Requirement", "ФВ-6.6")]
    public void Порожня_прихована_комірка_книги_нічого_не_дає()
    {
        var (table, visible, hidden) = Table();

        var diff = Build(table, visible, hidden, hiddenValue: null, hide: true);

        Assert.Empty(diff.Rejected);
        Assert.DoesNotContain(diff.Changes, c => c.ColumnCode == hidden.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_заборони_порівняння_як_було_регресія()
    {
        var (table, visible, hidden) = Table();

        // Збіг — пропуск, не відмова й не зміна.
        var same = Build(table, visible, hidden, hiddenValue: Stored, hide: false);
        Assert.Empty(same.Rejected);
        Assert.DoesNotContain(same.Changes, c => c.ColumnCode == hidden.Code);

        // Відмінність — зміна з поточним значенням.
        var other = Build(table, visible, hidden, hiddenValue: Stored + 1, hide: false);
        var change = Assert.Single(other.Changes, c => c.ColumnCode == hidden.Code);
        Assert.Equal(Stored, change.OldValue);
        Assert.Equal(Stored + 1, change.NewValue);
    }

    private static string Describe(TableDiff diff)
        => string.Join(
            "|",
            diff.Changes.Select(c => $"C:{c.RowKey}:{c.ColumnCode}:{c.OldValue}:{c.NewValue}")
                .Concat(diff.Rejected.Select(r => $"R:{r.RowKey}:{r.ColumnCode}:{r.ReasonCode}:{r.MessageKey}:{r.Message}")));

    private static (TableDef Table, ColumnDef Visible, ColumnDef Hidden) Table()
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var visible = builder.Column(table, "C1", CellDataType.Decimal);
        var hidden = builder.Column(table, "C2", CellDataType.Decimal);
        builder.Row(table, RowKey, 1);

        return (table, visible, hidden);
    }

    private static TableDiff Build(TableDef table, ColumnDef visible, ColumnDef hidden, decimal? hiddenValue, bool hide)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        worksheet.Cell(2, 1).Value = 1;
        if (hiddenValue is { } value)
        {
            worksheet.Cell(2, 2).Value = value;
        }

        // Видима C1 порожня в базі — у книзі 1: звичайна зміна.
        IReadOnlyList<CellRecord> current =
        [
            new CellRecord(new CellAddress(Period, RowId, hidden.Id), table.Id, new CellValueData { ValueNumeric = Stored }),
        ];

        return new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns:
                [
                    new ExcelColumnRef(visible.Id, visible.Code, 1, false, null),
                    new ExcelColumnRef(hidden.Id, hidden.Code, 2, false, null),
                ],
                Rows: [new ExcelRowRef(RowKey, 2)]),
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { [RowKey] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { [RowKey] = "0x0A" },
            current,
            hide ? id => id != hidden.Id : null);
    }
}
