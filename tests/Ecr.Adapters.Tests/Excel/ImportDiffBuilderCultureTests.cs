using System.Globalization;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel;

/// <summary>
/// Аудит `C1` (enterprise, 2026-09-28): число з книги не залежить від культури
/// сервера.
/// </summary>
/// <remarks>
/// ⛔ Доти числова комірка йшла через <c>cell.GetString()</c>, що форматує
/// <c>double</c> ПОТОЧНОЮ культурою, а далі — <c>decimal.TryParse(…,
/// NumberStyles.Number, InvariantCulture)</c>. На сервері з uk-UA 12.5 ставало
/// «12,5», кома читалася як роздільник тисяч — і в базу йшло 125. А 0.00001
/// («1E-05») відхилялось як «не число» за будь-якої культури.
/// </remarks>
public sealed class ImportDiffBuilderCultureTests
{
    private const long TableInstance = 500;
    private const int PeriodKeyValue = 202601;
    private const long RowId = 1001;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    public void Числа_з_книги_читаються_однаково_під_культурою_з_десятковою_комою()
    {
        // ⛔ Мутація: повернути `GetString()` + `TryParse(NumberStyles.Number)`
        // для числових комірок — 12.5 стане 125, а 0.00001 — відмовою.
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("uk-UA");
            CultureInfo.CurrentUICulture = new CultureInfo("uk-UA");

            var (table, columns) = Table(("N1", CellDataType.Decimal), ("N2", CellDataType.Decimal),
                                         ("T1", CellDataType.Decimal), ("T2", CellDataType.Decimal));

            using var workbook = Reopen(worksheet =>
            {
                worksheet.Cell(2, 1).Value = 12.5;
                worksheet.Cell(2, 2).Value = 0.00001;

                // Текстові комірки лишаються на шляху розбору тексту.
                worksheet.Cell(2, 3).SetValue("7.25");
                worksheet.Cell(2, 4).SetValue("2E-05");
            });

            Assert.Equal(XLDataType.Number, workbook.Worksheet("S0").Cell(2, 1).DataType);
            Assert.Equal(XLDataType.Text, workbook.Worksheet("S0").Cell(2, 3).DataType);

            var diff = Build(workbook.Worksheet("S0"), table, columns);

            var values = diff.Changes.ToDictionary(change => change.ColumnCode, change => change.NewValue);
            Assert.Equal(12.5m, Assert.IsType<decimal>(values["N1"]));
            Assert.Empty(diff.Rejected);
            Assert.Equal(0.00001m, Assert.IsType<decimal>(values["N2"]));
            Assert.Equal(7.25m, Assert.IsType<decimal>(values["T1"]));
            Assert.Equal(0.00002m, Assert.IsType<decimal>(values["T2"]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    public void Число_з_17_значущими_цифрами_не_обрізається_до_15()
    {
        // ⛔ Мутація: замінити круговий "R"-шлях на `(decimal)double` — той
        // округлює до 15 значущих цифр, і 123456789.12345679 стане
        // 123456789.123457: тиха втрата точності, якої текстовий шлях не мав.
        //
        // ⚠ Книга тут навмисно лише в пам'яті: `SaveAs` ClosedXML сам пише
        // число 15 значущими цифрами, і тест перевіряв би бібліотеку, а не
        // читання. Справжній Excel зберігає всі 17.
        var (table, columns) = Table(("N1", CellDataType.Decimal));

        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("S0").Cell(2, 1).Value = 123456789.12345679;

        var diff = Build(workbook.Worksheet("S0"), table, columns);

        Assert.Empty(diff.Rejected);
        Assert.Equal(123456789.12345679m, Assert.IsType<decimal>(Assert.Single(diff.Changes).NewValue));
    }

    /// <summary>Культури сервера, під якими правило коми мусить бути однаковим.</summary>
    private static readonly string[] Cultures = ["uk-UA", ""];

    /// <summary>
    /// Текст, який кома робить нечитабельним однозначно: (пункт правила, текст,
    /// чи це саме неоднозначний роздільник).
    /// </summary>
    private static readonly (string Rule, string Text, bool Ambiguous)[] RejectedTexts =
    [
        ("2a", "1,234", true),
        ("2a", "-1,234", true),
        ("2a", "12,345", true),
        ("5", "1,23,4", false),
        ("5", "1.234,5", false),
        ("5", "12,5.3", false),
        ("5", "1,2345.6", false),
    ];

    /// <summary>Текст, який читається однозначно: (пункт правила, текст, число Invariant).</summary>
    private static readonly (string Rule, string Text, string Expected)[] AcceptedTexts =
    [
        ("1", "7.25", "7.25"),
        ("1", "-2E-05", "-0.00002"),
        ("2b", "12,5", "12.5"),
        ("2b", "-12,5", "-12.5"),
        ("2b", "1234,567", "1234.567"),
        ("2b", "0,00001", "0.00001"),
        ("2b", "1,2345", "1.2345"),
        ("3", "1,234,567", "1234567"),
        ("3", "1,234,567.5", "1234567.5"),
        ("3", "-12,345,678.25", "-12345678.25"),
        ("4", "1,234.5", "1234.5"),
        ("4", "-1,234.5", "-1234.5"),
    ];

    public static TheoryData<string, string, string, bool> Rejected()
    {
        var data = new TheoryData<string, string, string, bool>();

        foreach (var culture in Cultures)
        {
            foreach (var (rule, text, ambiguous) in RejectedTexts)
            {
                data.Add(culture, rule, text, ambiguous);
            }
        }

        return data;
    }

    public static TheoryData<string, string, string, string> Accepted()
    {
        var data = new TheoryData<string, string, string, string>();

        foreach (var culture in Cultures)
        {
            foreach (var (rule, text, expected) in AcceptedTexts)
            {
                data.Add(culture, rule, text, expected);
            }
        }

        return data;
    }

    /// <summary>
    /// Кома в ТЕКСТОВІЙ комірці, яку не можна прочитати однозначно, — відмова
    /// <see cref="ImportMessageKeys.ExpectsNumber"/>, а не вгадане число.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутації:
    /// <list type="bullet">
    /// <item>повернути голий <c>NumberStyles.Number</c> (з <c>AllowThousands</c>)
    /// без <c>WithoutComma</c> — «1,234» і «1,23,4» знову мовчки стануть 1234;</item>
    /// <item>прибрати пункт 2а — «1,234» стане 1.234, хоча для людини з
    /// крапкою-роздільником це 1234.</item>
    /// </list>
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Rejected))]
    public void Кома_яку_не_можна_прочитати_однозначно_відхиляється(
        string cultureName, string rule, string text, bool ambiguous)
    {
        _ = rule;
        var diff = WithCulture(cultureName, () => BuildText(text));

        Assert.Empty(diff.Changes);
        var rejection = Assert.Single(diff.Rejected);
        Assert.Equal(CellValueReader.TypeMismatch, rejection.ReasonCode);
        Assert.Equal(ImportMessageKeys.ExpectsNumber, rejection.MessageKey);
        Assert.Equal(ambiguous, rejection.Message.Contains("ambiguous separator", StringComparison.Ordinal));

        // T2-13: ключ каталогу не лізе в людський текст (його несе MessageKey).
        Assert.DoesNotContain("err.", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ECR-", rejection.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Однозначний текст читається тим самим числом під будь-якою культурою:
    /// кома — десяткова, коли групуванням бути не може, і роздільник тисяч,
    /// коли інакше прочитати не можна.
    /// </summary>
    /// <remarks>
    /// ⛔ Мутація: голий <c>AllowThousands</c> — «12,5» стане 125, «1234,567» —
    /// 1234567.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Accepted))]
    public void Однозначний_текст_читається_однаково_під_будь_якою_культурою(
        string cultureName, string rule, string text, string expected)
    {
        _ = rule;
        var diff = WithCulture(cultureName, () => BuildText(text));

        Assert.Empty(diff.Rejected);
        Assert.Equal(
            decimal.Parse(expected, CultureInfo.InvariantCulture),
            Assert.IsType<decimal>(Assert.Single(diff.Changes).NewValue));
    }

    private static TableDiff BuildText(string text)
    {
        var (table, columns) = Table(("T1", CellDataType.Decimal));

        using var workbook = new XLWorkbook();
        workbook.Worksheets.Add("S0").Cell(2, 1).SetValue(text);

        return Build(workbook.Worksheet("S0"), table, columns);
    }

    private static T WithCulture<T>(string name, Func<T> action)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);

            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }

    /// <summary>Книга проходить через справжній <c>.xlsx</c>, а не лише через об'єкт у пам'яті.</summary>
    private static XLWorkbook Reopen(Action<IXLWorksheet> fill)
    {
        using var stream = new MemoryStream();

        using (var source = new XLWorkbook())
        {
            fill(source.Worksheets.Add("S0"));
            source.SaveAs(stream);
        }

        stream.Position = 0;

        return new XLWorkbook(stream);
    }

    private static (TableDef Table, IReadOnlyList<ColumnDef> Columns) Table(params (string Code, CellDataType Type)[] specs)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        var columns = specs.Select(spec => builder.Column(table, spec.Code, spec.Type)).ToList();
        builder.Row(table, "R1", 1);

        return (table, columns);
    }

    private static TableDiff Build(IXLWorksheet worksheet, TableDef table, IReadOnlyList<ColumnDef> columns)
        => new ImportDiffBuilder().Build(
            worksheet,
            new ExcelTableBlock(
                TableInstance, table.Id, table.Code, "S0", HeaderRow: 1,
                Columns: [.. columns.Select((column, index) => new ExcelColumnRef(column.Id, column.Code, index + 1, false, null))],
                Rows: [new ExcelRowRef("R1", 2)]),
            PeriodKeyValue,
            table,
            new Dictionary<CellAddress, EditDecision>(),
            new Dictionary<int, IReadOnlyDictionary<string, long>>(),
            new Dictionary<string, long>(StringComparer.Ordinal) { ["R1"] = RowId },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["R1"] = "0x0A" },
            []);
}
