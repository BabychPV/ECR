using ClosedXML.Excel;

namespace Ecr.Adapters.Tests.Excel.RegistryImport;

/// <summary>
/// A SYNTHETIC workbook with the structure of the ERA table 3.3 form (the file the legacy HSE 401 import reads) and the same
/// counts: 373 sources, 2,624 substance rows, 55 distinct substances, 27 group rows.
/// </summary>
/// <remarks>
/// The real file (<c>7) Параметры_2024_final.xlsx</c>) is NOT in the repository: its document properties name people and its
/// content is a company's emission filing with the coordinates of offshore installations. This generator reproduces what the
/// importer must survive, with invented data and no PRNG (everything derives from the indexes, so the model is exact):
/// three-row merged header with Latin look-alikes and double spaces, a column-number row, a group row per site, A-T merged
/// vertically per source (W-AB never), text-numbers, codes mixing <c>'0333'</c> and <c>2735</c>, values down to 3.5e-12,
/// area sources (<c>П1</c>) without diameter, a title cell inside the header window, blank rows and a totals block of formulas.
/// </remarks>
internal static class SyntheticEraTable33
{
    public const int Sources = 373;
    public const int SubstanceRows = 2624;
    public const int DistinctSubstances = 55;
    public const int Groups = 27;

    internal sealed record SubstanceModel(string Code, string Name, decimal? GramsPerSecond, decimal? MgPerNm3, decimal? TonsPerYear);

    internal sealed record SourceModel(
        int Index, int Group, string Nae, string Name, decimal Quantity, decimal Hours, string PointName, string Type,
        decimal Height, decimal? Diameter, decimal? Velocity, decimal? Volume, decimal Temperature,
        decimal X1, decimal Y1, decimal? X2, decimal? Y2, string Production, List<SubstanceModel> Substances);

    internal sealed record Workbook(byte[] Bytes, IReadOnlyList<SourceModel> Model, IReadOnlyList<string> GroupLabels, int FirstSourceRow, int LastDataRow);

    /// <summary>Substance rows per source: 1..20, 2,624 in total.</summary>
    internal static int[] SubstanceCounts()
    {
        var counts = new int[Sources];
        var sum = 0;
        for (var i = 0; i < Sources; i++)
        {
            counts[i] = 1 + (((i * 5) + 3) % 12);
            sum += counts[i];
        }

        for (var i = 0; sum != SubstanceRows; i = (i + 1) % Sources)
        {
            if (sum < SubstanceRows && counts[i] < 20)
            {
                counts[i]++;
                sum++;
            }
            else if (sum > SubstanceRows && counts[i] > 1)
            {
                counts[i]--;
                sum--;
            }
        }

        return counts;
    }

    private static int SubstanceNumber(int idx) => 301 + (idx * 37);

    private static string SubstanceCode(int idx) => SubstanceNumber(idx).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    internal static int NaeNumber(int i) => 1 + (i * 3);

    internal static string NaeKey(int i) => i == 100 ? "0036_pod" : NaeNumber(i).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);

    public static Workbook Build()
    {
        var counts = SubstanceCounts();
        var model = new List<SourceModel>(Sources);
        var perGroup = Enumerable.Range(0, Groups).Select(g => g < Sources % Groups ? (Sources / Groups) + 1 : Sources / Groups).ToArray();
        var group = 0;
        var inGroup = 0;

        for (var i = 0; i < Sources; i++)
        {
            if (inGroup == perGroup[group])
            {
                group++;
                inGroup = 0;
            }

            inGroup++;
            var area = i % 4 == 3;
            var subs = new List<SubstanceModel>();

            for (var j = 0; j < counts[i]; j++)
            {
                var idx = ((i * 7) + j) % DistinctSubstances;
                decimal? gs = (i + j) % 97 == 0 ? 3.5e-12m : (i * j) % 53 == 0 && j > 0 ? (decimal?)null : (((i * 31) + (j * 17)) % 1000 + 1) / 1000m;
                decimal? mg = (i + j) % 3 == 0 ? (((i * 11) + j) % 400) / 10m : (decimal?)null;
                decimal? tons = i == 5 && j == 0 ? (decimal?)null : (((i * 13) + (j * 7)) % 5000 + 1) / 1000m;
                subs.Add(new SubstanceModel(SubstanceCode(idx), $"Substance {idx:D2} (test)", gs, mg, tons));
            }

            model.Add(new SourceModel(
                i, group, NaeKey(i), $"Test source {i:D3}", 1 + (i % 9), i % 2 == 0 ? 8784 : 2880 + (i % 5 * 100), i % 3 == 0 ? "Exhaust duct" : "Unorganised source",
                area ? "P1" : "T",
                i % 3 == 0 ? 7 + (i % 4) : 9.5m + (i % 4) / 10m,
                area ? null : 0.25m + (i % 6) * 0.05m,
                area ? null : 3.63m + i % 5,
                area ? null : 0.178m + (i % 7) / 100m,
                34.1m + (i % 3),
                9590000m + (i * 13), 5140000m + (i * 17),
                area ? 2 + (i % 5) : null, area ? 1 + (i % 3) : null,
                (101 + (group * 4)).ToString(System.Globalization.CultureInfo.InvariantCulture), subs));
        }

        return Write(model);
    }

    private static Workbook Write(List<SourceModel> model)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");

        // ---- title rows and header (rows 1-7) ----
        ws.Cell(1, 1).Value = "ERA-like test form v0  Test Company LLP";
        ws.Range(1, 1, 1, 10).Merge();
        ws.Cell(1, 27).Value = "Table 3.3";
        ws.Range(1, 27, 1, 28).Merge();
        ws.Cell(2, 1).Value = "Parameters of emissions of pollutants into the atmosphere (synthetic)";
        ws.Range(2, 1, 2, 10).Merge();
        ws.Cell(3, 1).Value = "Test sea (test field), NDV-2024. Offshore complex (Parameters)";
        ws.Range(3, 1, 3, 13).Merge();

        void Head(int col, string text) { ws.Cell(4, col).Value = text; ws.Range(4, col, 6, col).Merge(); }

        Head(1, "Произ-водство");
        Head(2, "Цех");
        ws.Cell(4, 3).Value = "Источник выделения загрязняющих веществ";
        ws.Range(4, 3, 5, 4).Merge();
        ws.Cell(6, 3).Value = "Наименование";
        ws.Cell(6, 4).Value = "Количество, шт.";
        Head(5, "Число часов  работы в году");
        Head(6, "Наименование  источника выброса вредных веществ");
        Head(7, "Номер источника выбросов на карте-схеме");
        Head(8, "Т И П    И З А");
        Head(9, "Высота источника выбросов, м");
        Head(10, "Диаметр устья трубы, м");
        ws.Cell(4, 11).Value = "Параметры газовоздушной смеси на выходе из трубы при максимальной разовой нагрузке";
        ws.Range(4, 11, 5, 13).Merge();
        ws.Cell(6, 11).Value = "Скорость, м/с";
        ws.Cell(6, 12).Value = "Объем смеси,                м3/с";
        ws.Cell(6, 13).Value = "Темпе-ратура смеси, оС";
        ws.Cell(4, 14).Value = "Координаты источника на карте-схеме,м.";
        ws.Range(4, 14, 4, 17).Merge();
        ws.Cell(5, 14).Value = "точ.ист, /1-го конца линейного источника /центра площадного";
        ws.Range(5, 14, 5, 15).Merge();
        ws.Cell(5, 16).Value = "2-го конца линейного источника / длина, ширина площадного";
        ws.Range(5, 16, 5, 17).Merge();
        ws.Cell(6, 14).Value = "Х1";
        ws.Cell(6, 15).Value = "Y1";       // Latin Y, as in the real file
        ws.Cell(6, 16).Value = "Х2";
        ws.Cell(6, 17).Value = "Y2";
        Head(18, "Угол поворота площадного источника, град.");
        Head(19, "Hаименование газоочистных установок, тип и мероприятия по сокращению выбросов");   // Latin H
        Head(20, "Вещество, по которому производится газоочистка");
        Head(21, "Коэффи-циент обеспечен-ности газо-очисткой, %");
        Head(22, "Среднеэксплуа-тационная степень очистки/            максимальная степень очистки, %");
        Head(23, "Код вещества");
        Head(24, "Hаименование вещества");  // Latin H
        ws.Cell(4, 25).Value = "Выбросы загрязняющего вещества";
        ws.Range(4, 25, 5, 27).Merge();
        ws.Cell(6, 25).Value = "г/с";
        ws.Cell(6, 26).Value = "мг/нм3";
        ws.Cell(6, 27).Value = "т/год";
        Head(28, "Год дости-жения НДВ");

        var numbers = new[] { "1", "2", "3", "4", "5", "6", "7", "7а", "8", "9", "10", "11", "12", "13", "14", "15", "16", "16a", "17", "18", "19", "20", "21", "22", "23", "24", "25", "26" };
        for (var c = 0; c < numbers.Length; c++)
        {
            ws.Cell(7, c + 1).Value = numbers[c];
        }

        // ---- data ----
        var groupLabels = Enumerable.Range(0, Groups).Select(g => $"Test site {g + 1:D2}. Installations").ToList();
        var row = 8;
        var currentGroup = -1;
        var firstSourceRow = -1;

        foreach (var s in model)
        {
            if (s.Group != currentGroup)
            {
                currentGroup = s.Group;
                row++;
                ws.Cell(row, 1).Value = groupLabels[currentGroup];
                ws.Range(row, 1, row, 28).Merge();
            }

            var first = row + 1;
            firstSourceRow = firstSourceRow < 0 ? first : firstSourceRow;
            var last = first + s.Substances.Count - 1;

            // A-T: source level, merged over the substance rows. Numbers sometimes arrive as text, as in the real file.
            ws.Cell(first, 1).Value = int.Parse(s.Production, System.Globalization.CultureInfo.InvariantCulture);
            ws.Cell(first, 3).Value = s.Index % 5 == 0 ? s.Name.Replace(" ", "  ", StringComparison.Ordinal) : s.Index % 7 == 0 ? s.Name.Replace(" ", " ", StringComparison.Ordinal) : s.Name;
            SetNumberOrText(ws.Cell(first, 4), s.Quantity, s.Index % 2 == 1);
            SetNumberOrText(ws.Cell(first, 5), s.Hours, s.Index % 2 == 1);
            ws.Cell(first, 6).Value = s.PointName;
            SetNae(ws.Cell(first, 7), s);
            ws.Cell(first, 8).Value = s.Type == "P1" ? "П1" : s.Index % 10 == 0 ? "T" : "Т";   // Latin T for every tenth
            SetNumberOrText(ws.Cell(first, 9), s.Height, s.Index % 11 == 0);
            if (s.Diameter is { } d) { ws.Cell(first, 10).Value = (double)d; }
            if (s.Velocity is { } v) { ws.Cell(first, 11).Value = (double)v; }
            if (s.Volume is { } vol) { ws.Cell(first, 12).Value = (double)vol; }
            ws.Cell(first, 13).Value = (double)s.Temperature;
            ws.Cell(first, 14).Value = (double)s.X1;
            ws.Cell(first, 15).Value = (double)s.Y1;
            if (s.X2 is { } x2) { ws.Cell(first, 16).Value = (double)x2; }
            if (s.Y2 is { } y2) { ws.Cell(first, 17).Value = (double)y2; }

            if (last > first)
            {
                for (var c = 1; c <= 20; c++)
                {
                    ws.Range(first, c, last, c).Merge();
                }
            }

            // W-AB: per substance, never merged.
            for (var j = 0; j < s.Substances.Count; j++)
            {
                var sub = s.Substances[j];
                var r = first + j;
                var number = int.Parse(sub.Code, System.Globalization.CultureInfo.InvariantCulture);
                if (number < 1000)
                {
                    ws.Cell(r, 23).Value = sub.Code;           // text '0333'
                }
                else
                {
                    ws.Cell(r, 23).Value = number;             // number 2735
                }

                ws.Cell(r, 24).Value = sub.Name;
                if (sub.GramsPerSecond is { } gs) { ws.Cell(r, 25).Value = (double)gs; }
                if (sub.MgPerNm3 is { } mg) { ws.Cell(r, 26).Value = (double)mg; }
                if (sub.TonsPerYear is { } t)
                {
                    // A text-number now and then.
                    if ((s.Index + j) % 211 == 0) { ws.Cell(r, 27).Value = t.ToString(System.Globalization.CultureInfo.InvariantCulture); }
                    else { ws.Cell(r, 27).Value = (double)t; }
                }

                ws.Cell(r, 28).Value = 2024;
            }

            row = last;
        }

        var lastData = row;

        // ---- blank rows and the totals block (formulas) ----
        var total = row + 4;
        ws.Cell(total, 24).Value = "TOTAL";
        ws.Cell(total, 27).FormulaA1 = $"SUM(AA{total + 1}:AA{total + 4})";
        for (var k = 1; k <= 4; k++)
        {
            ws.Cell(total + k, 24).Value = $"Block {k}";
            ws.Cell(total + k, 27).FormulaA1 = $"SUM(AA9:AA{lastData})";
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new Workbook(ms.ToArray(), model, groupLabels, firstSourceRow, lastData);
    }

    private static void SetNumberOrText(IXLCell cell, decimal value, bool asText)
    {
        if (asText)
        {
            cell.Value = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            cell.Value = (double)value;
        }
    }

    private static void SetNae(IXLCell cell, SourceModel s)
    {
        if (s.Index == 100)
        {
            cell.Value = "36_pod";                  // NN_suffix code
        }
        else if (s.Index % 3 == 0)
        {
            cell.Value = s.Nae;                     // text '0004'
        }
        else
        {
            cell.Value = NaeNumber(s.Index);        // number 4
        }
    }
}
