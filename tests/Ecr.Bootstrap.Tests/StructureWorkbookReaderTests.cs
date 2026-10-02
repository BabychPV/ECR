using ClosedXML.Excel;
using Ecr.Bootstrap.Excel;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Bootstrap.Tests;

/// <summary>
/// Читання структури з синтетичних книг (ФВ-2.10, ФВ-2.11, ФВ-16.12).
/// </summary>
/// <remarks>
/// ⛔ Кожна книга будується в тесті, а не лежить бінарним файлом: видно, ЯКУ
/// саме особливість книги перевіряє тест, і її можна змінити одним рядком.
///
/// Мутаційні точки: прибери в <c>ReadTable</c> звіт про нерозпізнану одиницю —
/// червоніє <see cref="Нерозпізнана_одиниця_йде_у_звіт_а_колонка_без_одиниці"/>;
/// прибери вимогу «один вираз на всі рядки» — червоніє
/// <see cref="Різні_формули_в_рядках_тіла_не_стають_формулою_колонки"/>;
/// прибери вимогу «формула в КОЖНОМУ рядку» — червоніє
/// <see cref="Формула_лише_в_одному_рядку_не_стає_формулою_колонки"/>.
/// </remarks>
public sealed class StructureWorkbookReaderTests
{
    private static (StructurePlan Plan, ImportReport Report) Read(XLWorkbook wb, ReaderOptions? options = null)
    {
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        stream.Position = 0;
        return new StructureWorkbookReader().Read(stream, "synthetic.xlsx", options);
    }

    /// <summary>Типова таблиця шаблону: назва, шапка з одиницями, підписи рядків, формульна колонка, підсумок.</summary>
    private static XLWorkbook EmissionsWorkbook()
    {
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Викиди");
        ws.Cell("A1").Value = "Таблиця 1. Викиди забруднюючих речовин";
        ws.Cell("A2").Value = "Речовина";
        ws.Cell("B2").Value = "Потужність (г/с)";
        ws.Cell("C2").Value = "Години роботи";
        ws.Cell("D2").Value = "Викид, т/рік";

        string[] substances = ["Тверді частки", "Оксиди азоту", "Діоксид сірки"];
        for (var i = 0; i < substances.Length; i++)
        {
            var row = 3 + i;
            ws.Cell(row, 1).Value = substances[i];
            ws.Cell(row, 2).Style.NumberFormat.Format = "0.000";
            ws.Cell(row, 3).Style.NumberFormat.Format = "0";
            ws.Cell(row, 4).FormulaA1 = $"B{row}*C{row}*3.6/1000";
        }

        ws.Cell("A6").Value = "Всього";
        ws.Cell("D6").FormulaA1 = "SUM(D3:D5)";
        return wb;
    }

    [Fact]
    public void Типова_таблиця_дає_фіксовані_рядки_типи_одиниці_і_формулу()
    {
        using var wb = EmissionsWorkbook();

        var (plan, report) = Read(wb);

        Assert.False(report.HasErrors);
        var sheet = Assert.Single(plan.Sheets);
        Assert.Equal("VYKYDY", sheet.Code);
        var table = Assert.Single(sheet.Tables);
        Assert.Equal("Таблиця 1. Викиди забруднюючих речовин", table.Name);
        Assert.Equal(TableRowMode.Fixed, table.RowMode);
        Assert.Equal("Викиди!A2:D6", table.Address);

        Assert.Equal(["POTUZHNIST", "HODYNY_ROBOTY", "VYKYD"], table.Columns.Select(c => c.Code));
        Assert.Equal("g_per_s", table.Columns[0].UnitCode);
        Assert.Equal(CellDataType.Decimal, table.Columns[0].DataType);
        Assert.Equal((byte)3, table.Columns[0].Scale);
        Assert.Null(table.Columns[1].UnitCode);
        Assert.Equal("t_per_year", table.Columns[2].UnitCode);

        Assert.Equal(["Тверді частки", "Оксиди азоту", "Діоксид сірки", "Всього"], table.Rows.Select(r => r.Label));
        Assert.Equal(RowKind.Balance, table.Rows[3].Kind);
        Assert.All(table.Rows.Take(3), r => Assert.Equal(RowKind.Item, r.Kind));
    }

    [Fact]
    public void Формула_тіла_стає_формулою_колонки_а_підсумок_іде_у_звіт()
    {
        using var wb = EmissionsWorkbook();

        var (plan, report) = Read(wb);

        // Рядки речовин мають однакову формулу рядка — це формула колонки. Рядок
        // «Всього» має вертикальну суму: її не вгадано, а названо у звіті.
        var column = plan.Tables.Single().Columns[2];
        Assert.Equal(CellDataType.Formula, column.DataType);
        Assert.Equal("[POTUZHNIST]*[HODYNY_ROBOTY]*3.6/1000", column.Formula);
        var issue = Assert.Single(report.Issues, i => i.Message.Contains("SUM(D3:D5)", StringComparison.Ordinal));
        Assert.Equal(IssueSeverity.ManualReview, issue.Severity);
        Assert.Equal("Викиди!D6", issue.Location);
        Assert.Contains("VSOHO", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Різні_формули_в_рядках_тіла_не_стають_формулою_колонки()
    {
        using var wb = EmissionsWorkbook();
        wb.Worksheet(1).Cell("D4").FormulaA1 = "B4*C4";

        var (plan, report) = Read(wb);

        var column = plan.Tables.Single().Columns[2];
        Assert.Null(column.Formula);
        Assert.Equal(CellDataType.Decimal, column.DataType);
        Assert.True(column.IsReadOnly);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.ManualReview
            && i.Message.Contains("2 різних формул", StringComparison.Ordinal));
    }

    [Fact]
    public void Неперекладна_формула_тіла_названа_за_своєю_коміркою()
    {
        using var wb = EmissionsWorkbook();
        wb.Worksheet(1).Cell("D5").FormulaA1 = "B5*Коефіцієнт";

        var (plan, report) = Read(wb);

        Assert.Null(plan.Tables.Single().Columns[2].Formula);
        var issue = Assert.Single(report.Issues, i => i.Message.Contains("Коефіцієнт", StringComparison.Ordinal));
        Assert.Equal("Викиди!D5", issue.Location);
    }

    [Fact]
    public void Однакова_формула_в_кожному_рядку_стає_формулою_колонки()
    {
        using var wb = EmissionsWorkbook();
        var ws = wb.Worksheet(1);
        ws.Row(6).Delete();

        var (plan, report) = Read(wb);

        var column = plan.Tables.Single().Columns[2];
        Assert.Equal(CellDataType.Formula, column.DataType);
        Assert.Equal("[POTUZHNIST]*[HODYNY_ROBOTY]*3.6/1000", column.Formula);
        Assert.DoesNotContain(report.Issues, i => i.Severity == IssueSeverity.ManualReview && i.Message.Contains("формул", StringComparison.Ordinal));
    }

    [Fact]
    public void Формула_лише_в_одному_рядку_не_стає_формулою_колонки()
    {
        using var wb = EmissionsWorkbook();
        var ws = wb.Worksheet(1);
        ws.Row(6).Delete();
        ws.Cell("D4").Value = 1.5;
        ws.Cell("D5").Value = 2.5;

        var (plan, report) = Read(wb);

        // Лише D3 має формулу, решта — введені значення: колонка вводиться, а
        // формулу одного рядка вирішує людина.
        var column = plan.Tables.Single().Columns[2];
        Assert.Equal(CellDataType.Decimal, column.DataType);
        Assert.Null(column.Formula);
        Assert.False(column.IsReadOnly);
        var issue = Assert.Single(report.Issues, i => i.Message.Contains("рядкова формула", StringComparison.Ordinal));
        Assert.Equal("Викиди!D3", issue.Location);
    }

    [Fact]
    [Trait("Requirement", "ФВ-16.12")]
    public void Нерозпізнана_одиниця_йде_у_звіт_а_колонка_без_одиниці()
    {
        using var wb = EmissionsWorkbook();
        wb.Worksheet(1).Cell("C2").Value = "Витрата палива (т/год)";

        var (plan, report) = Read(wb);

        var column = plan.Tables.Single().Columns[1];
        Assert.Null(column.UnitCode);
        Assert.Equal("Витрата палива", column.Header);
        var issue = Assert.Single(report.Issues, i => i.Severity == IssueSeverity.ManualReview && i.Message.Contains("т/год", StringComparison.Ordinal));
        Assert.Equal("Викиди!C2", issue.Location);
        Assert.Contains("ФВ-16.12", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Таблиця_Excel_динамічна_якщо_перша_колонка_не_підписи()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Journal");
        ws.Cell("B2").Value = "Дата";
        ws.Cell("C2").Value = "Маса (кг)";
        ws.Cell("D2").Value = "Коментар";
        ws.Cell("B3").Value = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Unspecified);
        ws.Cell("C3").Value = 12.5;
        ws.Cell("D3").Value = "перевірено";
        ws.Range("B2:D3").CreateTable("Журнал_зважувань");

        var (plan, report) = Read(wb);

        Assert.False(report.HasErrors);
        var table = plan.Tables.Single();
        Assert.Equal(TableOrigin.ExcelTable, table.Origin);
        Assert.Equal("Журнал_зважувань", table.Name);
        Assert.Equal(TableRowMode.Dynamic, table.RowMode);
        Assert.Empty(table.Rows);
        Assert.Equal([CellDataType.Date, CellDataType.Decimal, CellDataType.String], table.Columns.Select(c => c.DataType));
        Assert.Equal("kg", table.Columns[1].UnitCode);
    }

    [Fact]
    public void Ієрархія_рядків_з_відступів()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Паливо");
        ws.Cell("A1").Value = "Показник";
        ws.Cell("B1").Value = "Значення";
        ws.Cell("A2").Value = "Котельня";
        ws.Cell("A3").Value = "Котел 1";
        ws.Cell("A3").Style.Alignment.Indent = 1;
        ws.Cell("A4").Value = "Пальник 1.1";
        ws.Cell("A4").Style.Alignment.Indent = 2;
        ws.Cell("A5").Value = "Котел 2";
        ws.Cell("A5").Style.Alignment.Indent = 1;
        ws.Cell("A6").Value = "Факел";

        var (plan, _) = Read(wb);

        var rows = plan.Tables.Single().Rows;
        Assert.Equal([null, "KOTELNIA", "KOTEL_1", "KOTELNIA", null], rows.Select(r => r.ParentKey));
    }

    [Fact]
    public void Без_відступів_ієрархію_задають_групові_рядки()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Джерела");
        ws.Cell("A1").Value = "Джерело";
        ws.Cell("B1").Value = "Викид (т)";
        ws.Cell("A2").Value = "Цех 1";
        ws.Cell("A2").Style.Font.Bold = true;
        ws.Cell("A3").Value = "Труба 1";
        ws.Cell("A4").Value = "Труба 2";
        ws.Cell("A5").Value = "Разом";

        var (plan, _) = Read(wb);

        var rows = plan.Tables.Single().Rows;
        Assert.Equal([RowKind.Group, RowKind.Item, RowKind.Item, RowKind.Balance], rows.Select(r => r.Kind));
        Assert.Equal([null, "TSEKH_1", "TSEKH_1", null], rows.Select(r => r.ParentKey));
    }

    [Fact]
    public void Два_блоки_через_порожній_рядок_дві_таблиці_з_позначкою_для_звірки()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Звіт");
        ws.Cell("A1").Value = "Паливо";
        ws.Cell("A2").Value = "Вид";
        ws.Cell("B2").Value = "Витрата (т)";
        ws.Cell("A3").Value = "Газ";
        ws.Cell("A5").Value = "Відходи";
        ws.Cell("A6").Value = "Вид";
        ws.Cell("B6").Value = "Маса (т)";
        ws.Cell("A7").Value = "Шлам";

        var (plan, report) = Read(wb);

        Assert.Equal(["Паливо", "Відходи"], plan.Tables.Select(t => t.Name));
        Assert.Equal(["PALYVO", "VIDKHODY"], plan.Tables.Select(t => t.Code));
        Assert.All(plan.Tables, t => Assert.Equal(TableOrigin.UsedRangeBlock, t.Origin));
        Assert.Equal(2, report.Issues.Count(i => i.Severity == IssueSeverity.ManualReview && i.Message.Contains("межі таблиці", StringComparison.Ordinal)));
    }

    [Fact]
    public void Іменований_діапазон_задає_межі_таблиці()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Дані");
        ws.Cell("A1").Value = "Довідка: не таблиця";
        ws.Cell("C3").Value = "Речовина";
        ws.Cell("D3").Value = "ГДК (мг/м³)";
        ws.Cell("C4").Value = "Пил";
        ws.Cell("D4").Value = 0.5;
        wb.DefinedNames.Add("Gdk", ws.Range("C3:D4"));

        var (plan, _) = Read(wb);

        var table = plan.Tables.Single();
        Assert.Equal(TableOrigin.DefinedName, table.Origin);
        Assert.Equal("Дані!C3:D4", table.Address);
        Assert.Equal("mg_per_m3", table.Columns.Single().UnitCode);
    }

    [Fact]
    public void Прихований_аркуш_пропущено_порожня_книга_помилка()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Configuration");
        ws.Cell("A1").Value = "Ключ";
        ws.Cell("B1").Value = "Значення";
        ws.Cell("A2").Value = "x";
        ws.Visibility = XLWorksheetVisibility.VeryHidden;
        wb.AddWorksheet("Порожній");

        var (plan, report) = Read(wb);

        Assert.Empty(plan.Sheets);
        Assert.True(report.HasErrors);
        Assert.Contains(report.Issues, i => i.Location == "Configuration" && i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void Колонка_нумерації_пропускається_підписи_з_наступної()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Реєстр");
        ws.Cell("A1").Value = "№";
        ws.Cell("B1").Value = "Найменування";
        ws.Cell("C1").Value = "Кількість";
        ws.Cell("A2").Value = 1;
        ws.Cell("B2").Value = "Котел";
        ws.Cell("C2").Value = 2;
        ws.Cell("A3").Value = 2;
        ws.Cell("B3").Value = "Піч";
        ws.Cell("C3").Value = 1;

        var (plan, _) = Read(wb);

        var table = plan.Tables.Single();
        Assert.Equal(TableRowMode.Fixed, table.RowMode);
        Assert.Equal(["Котел", "Піч"], table.Rows.Select(r => r.Label));
        Assert.Equal(["KILKIST"], table.Columns.Select(c => c.Code));
    }

    [Fact]
    public void Режим_dynamic_робить_усі_колонки_колонками()
    {
        using var wb = EmissionsWorkbook();

        var (plan, _) = Read(wb, new ReaderOptions(RowModeChoice.Dynamic));

        var table = plan.Tables.Single();
        Assert.Equal(TableRowMode.Dynamic, table.RowMode);
        Assert.Equal(4, table.Columns.Count);
        Assert.Empty(table.Rows);
    }

    [Fact]
    public void Захищений_аркуш_заблоковані_комірки_лише_для_читання()
    {
        using var wb = EmissionsWorkbook();
        var ws = wb.Worksheet(1);
        ws.Range("B3:B5").Style.Protection.Locked = false;
        ws.Protect();

        var (plan, _) = Read(wb);

        var columns = plan.Tables.Single().Columns;
        Assert.False(columns[0].IsReadOnly);
        Assert.True(columns[1].IsReadOnly);
    }

    [Fact]
    public void Не_книга_відхиляється_перевіркою_безпеки()
    {
        using var stream = new MemoryStream("це не xlsx"u8.ToArray());

        var (plan, report) = new StructureWorkbookReader().Read(stream, "bad.xlsx");

        Assert.Empty(plan.Sheets);
        var error = Assert.Single(report.Issues);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Contains("безпеки", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.000", (byte)3)]
    [InlineData("#,##0.00", (byte)2)]
    [InlineData("0", (byte)0)]
    [InlineData("0.0;-0.0", (byte)1)]
    [InlineData("General", null)]
    [InlineData("@", null)]
    public void Масштаб_з_формату_числа(string format, byte? scale)
        => Assert.Equal(scale, StructureWorkbookReader.ScaleOf(format));
}
