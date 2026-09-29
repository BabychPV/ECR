using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Рішення людини 2026-09-29: ТЕКСТОВА комірка книги читається за мовою
/// користувача, що імпортує; числові комірки Excel від культури не залежать.
/// </summary>
/// <remarks>
/// ⛔ Мутації: <c>ExcelImporter</c> не передає культуру (або <c>ReadNumber</c> її
/// ігнорує) — «1,234» у ru стає відмовою замість 1.234, «1 234,5» — відмовою
/// замість 1234.5; прибрати <c>LookupCode</c> — числовий код 1.5 на uk-UA
/// сервері стає «1,5» і не знаходиться (борг `C1`).
/// </remarks>
public sealed class ImportDiffBuilderUserCultureTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const long RowId = 1001;
    private const int Registry = 11;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("ru", "1,234", "1.234")]
    [InlineData("ru", "1 234,5", "1234.5")]
    [InlineData("kz", "1234,5", "1234.5")]
    [InlineData("en", "1,234.5", "1234.5")]
    [InlineData("en", "1234.5", "1234.5")]
    public void Текстове_число_з_книги_читається_за_мовою(string language, string text, string expected)
    {
        var diff = BuildText(text, CellDataType.Decimal, NumberCulture.ForLanguage(language));

        Assert.Empty(diff.Rejected);
        Assert.Equal(
            decimal.Parse(expected, CultureInfo.InvariantCulture),
            Assert.IsType<decimal>(Assert.Single(diff.Changes).NewValue));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("en", "1,234", true)]
    [InlineData("en", "1.234,5", false)]
    [InlineData("ru", "1,234.5", false)]
    public void Неоднозначне_і_змішане_з_книги_відхиляється(string language, string text, bool ambiguous)
    {
        var diff = BuildText(text, CellDataType.Decimal, NumberCulture.ForLanguage(language));

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(CellValueReader.TypeMismatch, rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.ExpectsNumber, rejection.MessageKey);
        Assert.Equal(ambiguous, rejection.Message.Contains("ambiguous separator", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ціле_з_розрядами_текстом_читається_цілим()
    {
        var diff = BuildText("1 234", CellDataType.Int, NumberCulture.ForLanguage("ru"));

        Assert.Empty(diff.Rejected);
        Assert.Equal(1234, Assert.IsType<int>(Assert.Single(diff.Changes).NewValue));
    }

    /// <summary>Борг `C1`: числовий код довідника з дробом на сервері з uk-UA.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    public void Числовий_код_довідника_з_дробом_не_залежить_від_культури_сервера()
    {
        var saved = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");

            var builder = new TemplateBuilder { TemplateVersionId = 1 };
            var table = builder.Table(builder.Sheet("Water"), "Main");
            var lookup = builder.Column(table, "Class", CellDataType.Lookup);
            builder.Row(table, "R1", 1);
            lookup.SetLookup(Registry);

            using var workbook = new XLWorkbook();
            workbook.Worksheets.Add("S0").Cell(2, 1).Value = 1.5;

            var lookups = new Dictionary<int, IReadOnlyDictionary<string, long>>
            {
                [Registry] = new Dictionary<string, long>(StringComparer.Ordinal) { ["1.5"] = 7001 },
            };

            var diff = Build(workbook.Worksheet("S0"), table, [lookup], lookups, culture: null);

            Assert.Empty(diff.Rejected);
            Assert.Equal(7001L, Assert.Single(diff.Changes).NewValue);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static TableDiff BuildText(string text, CellDataType type, CultureInfo culture)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var table = builder.Table(builder.Sheet("Water"), "Main");
        var column = builder.Column(table, "T1", type);
        builder.Row(table, "R1", 1);

        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("S0").Cell(2, 1).SetValue(text);

        return Build(workbook.Worksheet("S0"), table, [column], new(), culture);
    }

    private static TableDiff Build(
        IXLWorksheet worksheet,
        TableDef table,
        IReadOnlyList<ColumnDef> columns,
        Dictionary<int, IReadOnlyDictionary<string, long>> lookups,
        CultureInfo? culture)
        => new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns: [.. columns.Select((column, index) => new ExcelColumnRef(column.Id, column.Code, index + 1, false, null))],
                Rows: [new ExcelRowRef("R1", 2)]),
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            lookups,
            new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["R1"] = "0x0A" },
            [],
            culture: culture);
}
