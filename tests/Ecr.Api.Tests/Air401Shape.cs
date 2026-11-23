using ClosedXML.Excel;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-569 (P-1123-1): a workbook of EXACTLY the shape of the human's test file «7) Параметры_2024_final_Test.xlsx» (ERA v3.0,
/// table 3.3): title rows 1–3, header rows 4–6 with the same texts and merges (B4:B6 merged «Цех»), the column-number row 7
/// (with «7а» and «16a»), a group row, source rows with their substance rows, and totals rows whose t/year cell is a formula.
/// </summary>
/// <remarks>
/// ⛔ The customer's file itself is NOT committed: it carries a sensitivity label («Internal») and customer data, and the repository
/// is public. Its layout is copied cell by cell (dumped 23.11 from the file the human used), the values are neutral.
/// </remarks>
internal static class Air401Shape
{
    /// <summary>One substance row of a source.</summary>
    internal sealed record Substance(string Code, string Name, decimal GramsPerSecond, decimal MgPerNm3, decimal TonsPerYear);

    /// <summary>One source (NAE) row: <paramref name="Workshop"/> goes to column B («Цех»).</summary>
    internal sealed record Source(string Nae, string? Workshop, string Name, decimal Height, IReadOnlyList<Substance> Substances, string? Production = "101");

    private static readonly string[] Merges =
    [
        "A8:AB8", "J4:J6", "AB4:AB6", "Y4:AA5", "T4:T6", "V4:V6", "W4:W6", "K4:M5", "X4:X6", "N5:O5", "N4:Q4", "P5:Q5", "U4:U6",
        "AA1:AB1", "AA2:AB2", "A2:J2", "A4:A6", "B4:B6", "R4:R6", "H4:H6", "C4:D5", "E4:E6", "F4:F6", "S4:S6", "A1:J1", "A3:M3",
        "G4:G6", "I4:I6",
    ];

    /// <summary>The workbook bytes: the header of the test file, one group row, the sources, then the five totals rows.</summary>
    /// <param name="sources">Source rows in file order.</param>
    /// <param name="workshopHeader">Text of the merged header B4:B6 (the test file: «Цех»).</param>
    public static byte[] Build(IReadOnlyList<Source> sources, string workshopHeader = "Цех")
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Лист1");

        sheet.Cell("A1").Value = "ЭРА v3.0  ТОО \"TEST\"";
        sheet.Cell("AA1").Value = "Таблица 3.3";
        sheet.Cell("A2").Value = "Параметры выбросов загрязняющих веществ в атмосферу для расчета нормативов допустимых выбросов на 2024 ";
        sheet.Cell("A3").Value = "Тестовый объект, НДВ-2024 (Параметры)";

        void Put(string address, string text) => sheet.Cell(address).Value = text;
        Put("A4", "Произ-водство");
        Put("B4", workshopHeader);
        Put("C4", "Источник выделения загрязняющих веществ");
        Put("E4", "Число часов  работы в году");
        Put("F4", "Наименование  источника выброса вредных веществ");
        Put("G4", "Номер источника выбросов на карте-схеме");
        Put("H4", "Т И П    И З А");
        Put("I4", "Высота источника выбросов, м");
        Put("J4", "Диаметр устья трубы, м");
        Put("K4", "Параметры газовоздушной смеси на выходе из трубы при максимально разовой нагрузке");
        Put("N4", "Координаты источника на карте-схеме,м.");
        Put("R4", "Угол поворота площадного источника, град.");
        Put("S4", "Hаименование газоочистных установок, тип и мероприятия по сокращению выбросов");
        Put("T4", "Вещество, по которому производится газоочистка");
        Put("U4", "Коэффи-циент обеспечен-ности газо-очисткой, %");
        Put("V4", "Среднеэксплуа-тационная степень очистки/            максимальная степень очистки, %");
        Put("W4", "Код вещества");
        Put("X4", "Hаименование вещества");
        Put("Y4", "Выбросы загрязняющего вещества");
        Put("AB4", "Год дости-жения НДВ");
        Put("N5", "точ.ист, /1-го конца линейного источника /центра площадного источника");
        Put("P5", "2-го конца линейного источника / длина, ширина площадного источника");
        Put("C6", "Наименование");
        Put("D6", "Количество, шт.");
        Put("K6", "Скорость, м/с");
        Put("L6", "Объем смеси,                м3/с");
        Put("M6", "Темпе-ратура смеси, оС");
        Put("N6", "Х1");
        Put("O6", "Y1");
        Put("P6", "Х2");
        Put("Q6", "Y2");
        Put("Y6", "г/с");
        Put("Z6", "мг/нм3");
        Put("AA6", "т/год");

        // Row 7: the column numbers (1 … 26, «7а» after 7 and «16a» after 16) that close the header block.
        var number = 1;
        for (var c = 1; c <= 28; c++)
        {
            var letter = XLHelper.GetColumnLetterFromNumber(c);
            if (letter == "H")
            {
                sheet.Cell(7, c).Value = "7а";
            }
            else if (letter == "R")
            {
                sheet.Cell(7, c).Value = "16a";
            }
            else
            {
                sheet.Cell(7, c).Value = number++;
            }
        }

        sheet.Cell("A8").Value = "Технологические установки. Тестовая площадка";
        var row = 9;
        foreach (var source in sources)
        {
            if (source.Production is not null)
            {
                sheet.Cell(row, "A").Value = source.Production;
            }

            if (source.Workshop is not null)
            {
                sheet.Cell(row, "B").Value = source.Workshop;
            }

            sheet.Cell(row, "C").Value = source.Name;
            sheet.Cell(row, "D").Value = 1;
            sheet.Cell(row, "E").Value = 8784;
            sheet.Cell(row, "F").Value = "Вытяжной воздуховод";
            sheet.Cell(row, "G").Value = source.Nae;
            sheet.Cell(row, "H").Value = "Т";
            sheet.Cell(row, "I").Value = source.Height;
            sheet.Cell(row, "J").Value = 0.25;
            sheet.Cell(row, "K").Value = 3.63;
            sheet.Cell(row, "L").Value = 0.1781872;
            sheet.Cell(row, "M").Value = 34.1;
            sheet.Cell(row, "N").Value = 9597489;
            sheet.Cell(row, "O").Value = 5145573;
            foreach (var substance in source.Substances)
            {
                sheet.Cell(row, "W").Value = substance.Code;
                sheet.Cell(row, "X").Value = substance.Name;
                sheet.Cell(row, "Y").Value = substance.GramsPerSecond;
                sheet.Cell(row, "Z").Value = substance.MgPerNm3;
                sheet.Cell(row, "AA").Value = substance.TonsPerYear;
                sheet.Cell(row, "AB").Value = 2024;
                row++;
            }

            if (source.Substances.Count == 0)
            {
                row++;
            }
        }

        // The totals block of the test file: a label in X and a formula in AA (the file's formulas point at #REF!).
        row += 3;
        foreach (var label in new[] { "TOTAL", "Технологические установки", "Факелы", "Установки инженерного обеспечения", "Вспомогательные работы" })
        {
            sheet.Cell(row, "X").Value = label;
            sheet.Cell(row, "AA").FormulaA1 = "SUM(AA9:AA9)";
            row++;
        }

        foreach (var range in Merges)
        {
            sheet.Range(range).Merge();
        }

        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }
}
