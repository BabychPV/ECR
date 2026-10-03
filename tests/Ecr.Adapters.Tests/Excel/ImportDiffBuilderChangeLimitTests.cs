// tests/Ecr.Adapters.Tests/Excel/ImportDiffBuilderChangeLimitTests.cs
using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// L6-01 (аудит 2026-10-03): стеля <see cref="ImportDiffBuilder.MaxChanges"/> — відмова всього
/// перегляду, а не мовчазне обрізання до перших 5000 змін таблиці.
/// </summary>
/// <remarks>
/// ⛔ Доти на 5000-й зміні стояв <c>break</c> лише внутрішнього циклу: решта книги
/// відкидалася без позначки, план для Apply містив перші 5000, і Apply відповідав
/// успіхом — порушення DAT-05 «усе або нічого». Перевірка тут, у <c>Build</c>: план для
/// Apply будується саме з його результату, тож відмова тут = плану немає.
/// </remarks>
public sealed class ImportDiffBuilderChangeLimitTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "L6-01")]
    public void Зміна_понад_стелю_відхиляє_весь_перегляд_а_не_обрізає_до_перших_5000()
    {
        var (worksheet, block, table, rowIds, versions, workbook) = Arrange(ImportDiffBuilder.MaxChanges + 1, filled: ImportDiffBuilder.MaxChanges + 1);
        using var _ = workbook;

        var ex = Assert.Throws<BusinessRuleException>(() => new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table, NoDecisions, NoLookups, rowIds, versions, []));

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.NotNull(ex.Details);
        Assert.Equal(ImportMessageKeys.TooManyChanges, ex.Details!["messageKey"]);
        Assert.Equal("Main", ex.Details["tableCode"]);
        Assert.Equal(ImportDiffBuilder.MaxChanges, ex.Details["maxChanges"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "L6-01")]
    public void Рівно_стеля_змін_проходить_цілком()
    {
        // Межа: 5000 — ще дозволено, і ВСІ вони в плані.
        var (worksheet, block, table, rowIds, versions, workbook) = Arrange(ImportDiffBuilder.MaxChanges, filled: ImportDiffBuilder.MaxChanges);
        using var _ = workbook;

        var diff = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table, NoDecisions, NoLookups, rowIds, versions, []);

        Assert.Equal(ImportDiffBuilder.MaxChanges, diff.Changes.Count);
        Assert.Equal("R5000", diff.Changes[^1].RowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "L6-01")]
    public void Рядки_без_змін_понад_стелю_не_дають_відмови()
    {
        // Стеля рахує ЗМІНИ, а не комірки: доти перевірка стояла перед читанням комірки,
        // і після 5000-ї зміни цикл обривався, навіть коли далі змін не було. Тепер
        // порожній хвіст таблиці (рядки, яких у книзі не чіпали) не перетворюється на відмову.
        var (worksheet, block, table, rowIds, versions, workbook) = Arrange(ImportDiffBuilder.MaxChanges + 10, filled: ImportDiffBuilder.MaxChanges);
        using var _ = workbook;

        var diff = new ImportDiffBuilder().Build(
            worksheet, block, PeriodKeyValue, table, NoDecisions, NoLookups, rowIds, versions, []);

        Assert.Equal(ImportDiffBuilder.MaxChanges, diff.Changes.Count);
        Assert.Empty(diff.Rejected);
    }

    private static (IXLWorksheet Worksheet, ExcelTableBlock Block, TableDef Table,
        Dictionary<string, long> RowIds, Dictionary<string, string> Versions, XLWorkbook Workbook)
        Arrange(int rows, int filled)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var amount = builder.Column(table, "Amount", CellDataType.Decimal);

        var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("S0");
        var rowRefs = new List<ExcelRowRef>(rows);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i <= rows; i++)
        {
            var key = "R" + i.ToString(CultureInfo.InvariantCulture);
            builder.Row(table, key, i);
            rowRefs.Add(new ExcelRowRef(key, i + 1));
            rowIds[key] = 1000 + i;
            versions[key] = "0x0A";

            if (i <= filled)
            {
                worksheet.Cell(i + 1, 1).Value = i;
            }
        }

        var block = new ExcelTableBlock(
            TableInstance, TableDefId: 0, "T0", "S0", HeaderRow: 1,
            Columns: [new ExcelColumnRef(amount.Id, "Amount", 1, false, null)],
            Rows: rowRefs);

        return (worksheet, block, table, rowIds, versions, workbook);
    }

    private static Dictionary<CellAddress, EditDecision> NoDecisions => [];

    private static Dictionary<int, IReadOnlyDictionary<string, long>> NoLookups => [];
}
