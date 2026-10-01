using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Ecr.Adapters.Excel;
using Ecr.Domain.Enums;

namespace Ecr.Bootstrap.Excel;

/// <summary>Як визначати режим рядків таблиці.</summary>
public enum RowModeChoice
{
    /// <summary>
    /// Автоматично: перша колонка з текстом у кожному рядку даних — підписи
    /// фіксованих рядків; інакше таблиця динамічна.
    /// </summary>
    Auto,

    /// <summary>Завжди фіксовані рядки з підписами в першій колонці.</summary>
    Fixed,

    /// <summary>Завжди динамічні рядки: кожна колонка книги — колонка шаблону.</summary>
    Dynamic,
}

/// <summary>Налаштування читання книги.</summary>
/// <param name="RowMode">Режим рядків.</param>
/// <param name="Layout">Розкладка періодів для всіх таблиць.</param>
public sealed record ReaderOptions(RowModeChoice RowMode = RowModeChoice.Auto, TableLayoutKind Layout = TableLayoutKind.PerPeriodInstance);

/// <summary>
/// Читає структуру шаблону з <c>.xlsx</c>/<c>.xlsm</c> (ФВ-2.10, ФВ-2.11):
/// аркуші → таблиці → колонки (тип, одиниця, формула) → фіксовані рядки.
/// Дані комірок не імпортуються (<c>D-94</c>).
/// </summary>
/// <remarks>
/// Межі таблиць — у порядку довіри: таблиці Excel (ListObject), іменовані
/// діапазони, і лише коли їх на аркуші немає — блоки непорожніх рядків,
/// розділені порожнім рядком. Останній випадок — здогадка, тому кожна така
/// таблиця йде у звіт для звірки.
///
/// ⛔ Книга спершу проходить <see cref="XlsxSafetyGate"/> — той самий огляд,
/// що й імпорт даних у застосунку: архів-бомба чи зіпсований пакет
/// відхиляються до розбору, а не під час нього.
/// </remarks>
public sealed partial class StructureWorkbookReader
{
    private static readonly string[] MonthNames =
    [
        "січ", "лют", "бер", "кві", "тра", "чер", "лип", "сер", "вер", "жов", "лис", "гру",
        "янв", "фев", "мар", "апр", "мая", "май", "июн", "июл", "авг", "сен", "окт", "ноя", "дек",
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec",
    ];

    private readonly ExcelFormulaTranslator _translator = new();

    /// <summary>Читає книгу.</summary>
    /// <param name="workbook">Потік книги з підтримкою позиціювання.</param>
    /// <param name="fileName">Ім'я файлу для звіту.</param>
    /// <param name="options">Налаштування; <c>null</c> — за замовчуванням.</param>
    /// <returns>План структури і звіт.</returns>
    public (StructurePlan Plan, ImportReport Report) Read(Stream workbook, string fileName, ReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        options ??= new ReaderOptions();
        var report = new ImportReport();

        var verdict = XlsxSafetyGate.Inspect(workbook);
        if (verdict != XlsxVerdict.Safe)
        {
            report.Error(fileName, $"книгу відхилено перевіркою безпеки пакета: {verdict}");
            return (new StructurePlan(fileName, []), report);
        }

        using var wb = new XLWorkbook(workbook);
        var sheetCodes = new CodeGenerator();
        var sheets = new List<PlannedSheet>();

        foreach (var ws in wb.Worksheets.OrderBy(w => w.Position))
        {
            if (ws.Visibility != XLWorksheetVisibility.Visible)
            {
                report.Info(ws.Name, "прихований аркуш пропущено (службові аркуші legacy-книги не є структурою шаблону)");
                continue;
            }

            var regions = FindRegions(wb, ws, report);
            if (regions.Count == 0)
            {
                report.Info(ws.Name, "аркуш без таблиць пропущено");
                continue;
            }

            var tableCodes = new CodeGenerator();
            var tables = new List<PlannedTable>();
            foreach (var region in regions)
            {
                var table = ReadTable(ws, region, tables.Count + 1, tableCodes, report, options);
                if (table is not null)
                {
                    tables.Add(table);
                }
            }

            if (tables.Count > 0)
            {
                var ordinal = sheets.Count + 1;
                sheets.Add(new PlannedSheet(sheetCodes.Next(ws.Name, "S", ordinal), ws.Name, ordinal, tables));
            }
        }

        if (sheets.Count == 0)
        {
            report.Error(fileName, "у книзі не знайдено жодної таблиці: немає видимих аркушів із заголовком і даними");
        }

        return (new StructurePlan(fileName, sheets), report);
    }

    private sealed record Region(IXLRange Range, string? Name, TableOrigin Origin);

    private static List<Region> FindRegions(XLWorkbook wb, IXLWorksheet ws, ImportReport report)
    {
        var regions = ws.Tables
            .Select(t => new Region(t.AsRange(), t.Name, TableOrigin.ExcelTable))
            .ToList();

        foreach (var name in wb.DefinedNames.Concat(ws.DefinedNames))
        {
            if (name.Name.StartsWith("_xlnm", StringComparison.OrdinalIgnoreCase)
                || name.Name.StartsWith("Print_", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ranges = name.Ranges.Where(r => r.Worksheet.Name == ws.Name).ToList();
            if (ranges.Count == 0)
            {
                continue;
            }

            if (ranges.Count > 1)
            {
                report.Manual($"{ws.Name}!{name.Name}", "іменований діапазон із кількох областей — таблицю з нього не створено");
                continue;
            }

            var range = ranges[0];
            if (range.RowCount() < 2 || range.ColumnCount() < 1
                || regions.Any(r => r.Range.Intersects(range)))
            {
                continue;
            }

            regions.Add(new Region(range, name.Name, TableOrigin.DefinedName));
        }

        if (regions.Count > 0)
        {
            return [.. regions.OrderBy(r => r.Range.FirstRow().RowNumber()).ThenBy(r => r.Range.FirstColumn().ColumnNumber())];
        }

        return BlocksOfUsedRange(ws);
    }

    /// <summary>
    /// Блоки непорожніх рядків, розділені порожнім рядком. Рядок-блок з однією
    /// заповненою коміркою над таблицею — її назва, а не таблиця.
    /// </summary>
    private static List<Region> BlocksOfUsedRange(IXLWorksheet ws)
    {
        var used = ws.RangeUsed(XLCellsUsedOptions.Contents);
        var result = new List<Region>();
        if (used is null)
        {
            return result;
        }

        var firstColumn = used.FirstColumn().ColumnNumber();
        var lastColumn = used.LastColumn().ColumnNumber();
        string? pendingTitle = null;
        var blockStart = 0;

        void Close(int lastRow)
        {
            if (blockStart == 0)
            {
                return;
            }

            var start = blockStart;
            blockStart = 0;
            var filled = FilledCells(ws, start, firstColumn, lastColumn);

            if (lastRow == start && filled.Count == 1)
            {
                pendingTitle = filled[0].GetString().Trim();
                return;
            }

            // Назва безпосередньо над заголовком, без порожнього рядка між ними.
            if (lastRow - start >= 2 && filled.Count == 1
                && FilledCells(ws, start + 1, firstColumn, lastColumn).Count >= 2)
            {
                pendingTitle = filled[0].GetString().Trim();
                start++;
            }

            var cols = Enumerable.Range(start, lastRow - start + 1)
                .SelectMany(r => FilledCells(ws, r, firstColumn, lastColumn))
                .Select(c => c.Address.ColumnNumber)
                .ToList();
            var range = ws.Range(start, cols.Min(), lastRow, cols.Max());
            result.Add(new Region(range, pendingTitle, TableOrigin.UsedRangeBlock));
            pendingTitle = null;
        }

        for (var row = used.FirstRow().RowNumber(); row <= used.LastRow().RowNumber(); row++)
        {
            var blank = FilledCells(ws, row, firstColumn, lastColumn).Count == 0;
            if (blank)
            {
                Close(row - 1);
            }
            else if (blockStart == 0)
            {
                blockStart = row;
            }
        }

        Close(used.LastRow().RowNumber());
        return result;
    }

    private static List<IXLCell> FilledCells(IXLWorksheet ws, int row, int firstColumn, int lastColumn)
        => [.. Enumerable.Range(firstColumn, lastColumn - firstColumn + 1)
            .Select(c => ws.Cell(row, c))
            .Where(c => !c.IsEmpty(XLCellsUsedOptions.Contents))];

    private PlannedTable? ReadTable(
        IXLWorksheet ws, Region region, int ordinal, CodeGenerator tableCodes, ImportReport report, ReaderOptions options)
    {
        var range = region.Range;
        var firstRow = range.FirstRow().RowNumber();
        var lastRow = range.LastRow().RowNumber();
        var firstCol = range.FirstColumn().ColumnNumber();
        var lastCol = range.LastColumn().ColumnNumber();
        var address = $"{ws.Name}!{range.RangeAddress.ToStringRelative()}";

        // Дворівнева шапка: у першому рядку є об'єднання по горизонталі.
        var headerRows = 1;
        if (lastRow > firstRow + 1
            && Enumerable.Range(firstCol, lastCol - firstCol + 1).Any(c =>
                ws.Cell(firstRow, c).IsMerged() && ws.Cell(firstRow, c).MergedRange().ColumnCount() > 1))
        {
            headerRows = 2;
            report.Info(address, "дворівнева шапка: заголовок колонки складено як «верхній / нижній»");
        }

        var dataStart = firstRow + headerRows;
        var dataRows = Enumerable.Range(dataStart, Math.Max(0, lastRow - dataStart + 1)).ToList();

        var labelCol = ChooseLabelColumn(ws, firstCol, lastCol, firstRow, headerRows, dataRows, options, address, report);
        var rowMode = labelCol is null ? TableRowMode.Dynamic : TableRowMode.Fixed;
        var valueStart = labelCol is { } lc ? lc + 1 : firstCol;
        if (valueStart > lastCol)
        {
            report.Error(address, "таблиця без колонок даних: є лише колонка підписів рядків");
            return null;
        }

        var name = !string.IsNullOrWhiteSpace(region.Name) ? region.Name! : $"{ws.Name} {ordinal.ToString(CultureInfo.InvariantCulture)}";
        var code = tableCodes.Next(name, "T", ordinal);

        if (region.Origin == TableOrigin.UsedRangeBlock)
        {
            report.Manual(address, $"межі таблиці «{name}» визначено за порожніми рядками — звірте, чи це одна таблиця");
        }

        var rows = labelCol is { } label ? ReadRows(ws, label, valueStart, lastCol, dataRows, address, report) : [];
        var rowsByLine = rows.ToDictionary(r => r.Line);

        var columnCodes = new CodeGenerator();
        var headers = new List<(int Column, HeaderUnit Header, string Code)>();
        for (var c = valueStart; c <= lastCol; c++)
        {
            var raw = HeaderText(ws, c, firstRow, headerRows);
            var cellAddress = $"{ws.Name}!{ws.Cell(firstRow, c).Address.ToStringRelative()}";
            if (raw.Length == 0)
            {
                raw = $"Колонка {ws.Cell(firstRow, c).Address.ColumnLetter}";
                report.Manual(cellAddress, $"порожній заголовок колонки — названо «{raw}»");
            }

            var parsed = UnitRecognizer.Parse(raw);
            if (parsed.UnitText is not null && parsed.UnitCode is null)
            {
                report.Manual(cellAddress, $"одиницю «{parsed.UnitText}» не розпізнано ({parsed.Problem}); колонку створено без одиниці — задайте її в конструкторі (ФВ-16.12)");
            }

            headers.Add((c, parsed, columnCodes.Next(parsed.Header, "C", c - valueStart + 1)));
        }

        var codeByColumn = headers.ToDictionary(h => h.Column, h => h.Code);
        var columns = new List<PlannedColumn>();
        foreach (var (c, header, columnCode) in headers)
        {
            // Групові рядки фіксованої таблиці даних не мають — у виведенні типу їх не враховано.
            var lines = labelCol is null
                ? dataRows
                : [.. dataRows.Where(r => rowsByLine.TryGetValue(r, out var row) && row.Row.Kind != RowKind.Group)];
            columns.Add(ReadColumn(ws, c, lines, header, columnCode, columns.Count + 1, codeByColumn, rowsByLine, report));
        }

        var layout = options.Layout;
        if (headers.Count(h => IsMonth(h.Header.Header)) >= 12)
        {
            layout = TableLayoutKind.MonthsInColumns;
            report.Manual(address, "12 колонок-місяців: розкладку прийнято MonthsInColumns; позначте колонки місяців у конструкторі");
        }

        if (dataRows.Count == 0)
        {
            report.Info(address, "таблиця без рядків даних — створено динамічну таблицю лише з колонками");
        }

        return new PlannedTable(
            code, name, ordinal, address, region.Origin, layout, rowMode,
            columns, [.. rows.Select(r => r.Row)]);
    }

    private static int? ChooseLabelColumn(
        IXLWorksheet ws, int firstCol, int lastCol, int firstRow, int headerRows, List<int> dataRows,
        ReaderOptions options, string address, ImportReport report)
    {
        if (options.RowMode == RowModeChoice.Dynamic || dataRows.Count == 0 || firstCol == lastCol)
        {
            return null;
        }

        bool IsLabelColumn(int c) => dataRows.All(r =>
        {
            var cell = ws.Cell(r, c);
            return !cell.HasFormula && cell.Value.IsText && cell.GetString().Trim().Length > 0;
        });

        // «№ | Найменування | …»: колонка порядкових номерів не є даними шаблону.
        var header = HeaderText(ws, firstCol, firstRow, headerRows);
        var isNumbering = NumberingHeader().IsMatch(header)
            && dataRows.All(r => ws.Cell(r, firstCol).Value.IsNumber || ws.Cell(r, firstCol).IsEmpty(XLCellsUsedOptions.Contents));
        if (isNumbering && firstCol + 1 < lastCol && IsLabelColumn(firstCol + 1))
        {
            report.Info(address, $"колонку нумерації «{header}» пропущено: підписи рядків — у наступній колонці");
            return firstCol + 1;
        }

        if (options.RowMode == RowModeChoice.Fixed || IsLabelColumn(firstCol))
        {
            return firstCol;
        }

        report.Info(address, "перша колонка не є підписами в кожному рядку — таблицю створено з динамічними рядками");
        return null;
    }

    private sealed record LineRow(int Line, PlannedRow Row);

    private static List<LineRow> ReadRows(
        IXLWorksheet ws, int labelCol, int valueStart, int lastCol, List<int> dataRows, string address, ImportReport report)
    {
        var keys = new CodeGenerator();
        var result = new List<LineRow>();
        var stack = new Stack<(int Indent, string Key)>();
        var anyIndent = dataRows.Any(r => ws.Cell(r, labelCol).Style.Alignment.Indent > 0);
        string? currentGroup = null;

        foreach (var line in dataRows)
        {
            var cell = ws.Cell(line, labelCol);
            var label = cell.GetString().Trim();
            if (label.Length == 0)
            {
                report.Manual($"{ws.Name}!{cell.Address.ToStringRelative()}", "рядок без підпису пропущено");
                continue;
            }

            var valuesEmpty = Enumerable.Range(valueStart, lastCol - valueStart + 1)
                .All(c => ws.Cell(line, c).IsEmpty(XLCellsUsedOptions.Contents));
            var kind = TotalLabel().IsMatch(label) ? RowKind.Balance
                : cell.Style.Font.Bold && valuesEmpty ? RowKind.Group
                : RowKind.Item;

            var ordinal = result.Count + 1;
            var key = keys.Next(label, "R", ordinal);
            string? parent;

            if (anyIndent)
            {
                var indent = cell.Style.Alignment.Indent;
                while (stack.Count > 0 && stack.Peek().Indent >= indent)
                {
                    stack.Pop();
                }

                parent = stack.Count > 0 ? stack.Peek().Key : null;
                stack.Push((indent, key));
            }
            else
            {
                // Без відступів ієрархію задають групові рядки: усе до наступної групи — її діти.
                parent = kind == RowKind.Item ? currentGroup : null;
                if (kind == RowKind.Group)
                {
                    currentGroup = key;
                }
                else if (kind == RowKind.Balance)
                {
                    currentGroup = null;
                }
            }

            result.Add(new LineRow(line, new PlannedRow(key, label, ordinal, kind, parent)));
        }

        if (result.Any(r => r.Row.ParentKey is not null))
        {
            report.Info(address, anyIndent
                ? "ієрархію рядків узято з відступів підписів"
                : "ієрархію рядків узято з групових (жирних, без значень) рядків");
        }

        return result;
    }

    private PlannedColumn ReadColumn(
        IXLWorksheet ws, int column, List<int> lines, HeaderUnit header, string code, int ordinal,
        IReadOnlyDictionary<int, string> codeByColumn, Dictionary<int, LineRow> rowsByLine, ImportReport report)
    {
        var cells = lines.Select(r => ws.Cell(r, column)).ToList();
        var formulaCells = cells.Where(c => c.HasFormula).ToList();
        var valueCells = cells.Where(c => !c.HasFormula && !c.IsEmpty(XLCellsUsedOptions.Contents)).ToList();
        var location = $"{ws.Name}!{ws.Cell(lines.Count > 0 ? lines[0] : 1, column).Address.ToStringRelative()}";
        var sample = cells.FirstOrDefault();
        var isReadOnly = ws.IsProtected && cells.Count > 0 && cells.All(c => c.Style.Protection.Locked);
        var scale = sample is null ? null : ScaleOf(sample.Style.NumberFormat.Format);

        // Колонкова формула: формула в кожному рядку, крім підсумкових, і всі
        // вони перекладаються в ОДИН вираз. Підсумкові рядки («Всього») майже
        // завжди мають вертикальну суму — це рядкова формула, її вирішує людина.
        bool IsBalance(IXLCell c) => rowsByLine.TryGetValue(c.Address.RowNumber, out var line) && line.Row.Kind == RowKind.Balance;
        var bodyCells = cells.Where(c => !IsBalance(c)).ToList();
        if (bodyCells.Count > 0 && bodyCells.All(c => c.HasFormula) && formulaCells.Count == cells.Count)
        {
            var translations = bodyCells
                .Select(c => (Cell: c, T: _translator.Translate(c.FormulaA1, c.Address.RowNumber, codeByColumn)))
                .ToList();
            var failed = translations.FirstOrDefault(t => !t.T.IsSuccess);
            var distinct = translations.Where(t => t.T.IsSuccess).Select(t => t.T.Expression).Distinct(StringComparer.Ordinal).ToList();

            if (failed.Cell is null && distinct.Count == 1)
            {
                foreach (var total in cells.Where(IsBalance))
                {
                    var t = _translator.Translate(total.FormulaA1, total.Address.RowNumber, codeByColumn);
                    if (t.Expression != distinct[0])
                    {
                        report.Manual(
                            $"{ws.Name}!{total.Address.ToStringRelative()}",
                            $"підсумковий рядок {rowsByLine[total.Address.RowNumber].Row.Key} має власну формулу «={total.FormulaA1}» — задайте її рядковою формулою в конструкторі, інакше до нього застосується формула колонки");
                    }
                }

                return new PlannedColumn(code, header.Header, ordinal, CellDataType.Formula, scale, header.UnitCode, true, distinct[0]);
            }

            var why = failed.Cell is not null
                ? $"формулу «={failed.Cell.FormulaA1}» не перекладено: {failed.T.Problem}"
                : $"рядки мають {distinct.Count} різних формул — колонкова формула неоднозначна";
            var at = failed.Cell is not null ? $"{ws.Name}!{failed.Cell.Address.ToStringRelative()}" : location;
            report.Manual(at, $"{why}. Колонку «{header.Header}» створено як Decimal лише для читання — задайте формулу в конструкторі");
            return new PlannedColumn(code, header.Header, ordinal, CellDataType.Decimal, scale, header.UnitCode, true, null);
        }

        // Формули лише в окремих рядках (підсумки тощо) — рядкові формули вирішує людина.
        foreach (var f in formulaCells.Take(5))
        {
            var rowKey = rowsByLine.TryGetValue(f.Address.RowNumber, out var r) ? $" (рядок {r.Row.Key})" : string.Empty;
            report.Manual($"{ws.Name}!{f.Address.ToStringRelative()}", $"рядкова формула «={f.FormulaA1}»{rowKey} не імпортується автоматично — задайте її в конструкторі");
        }

        if (formulaCells.Count > 5)
        {
            report.Manual(location, $"ще {formulaCells.Count - 5} рядкових формул у колонці «{header.Header}»");
        }

        var kinds = valueCells.Select(c => c.DataType).Distinct().ToList();
        CellDataType type;
        if (kinds.Count == 0)
        {
            type = sample is not null && IsDateFormat(sample) ? CellDataType.Date
                : sample?.Style.NumberFormat.Format == "@" ? CellDataType.String
                : CellDataType.Decimal;
            report.Info(location, $"колонка «{header.Header}» без значень: тип {type} за форматом комірок");
        }
        else if (kinds.Count == 1)
        {
            type = kinds[0] switch
            {
                XLDataType.Number => CellDataType.Decimal,
                XLDataType.DateTime => CellDataType.Date,
                XLDataType.Boolean => CellDataType.Bool,
                _ => CellDataType.String,
            };
        }
        else
        {
            type = CellDataType.String;
            report.Manual(location, $"у колонці «{header.Header}» змішані типи значень ({string.Join(", ", kinds)}) — прийнято String");
        }

        return new PlannedColumn(
            code, header.Header, ordinal, type, type == CellDataType.Decimal ? scale : null,
            header.UnitCode, isReadOnly, null);
    }

    private static string HeaderText(IXLWorksheet ws, int column, int firstRow, int headerRows)
    {
        var parts = new List<string>();
        for (var r = firstRow; r < firstRow + headerRows; r++)
        {
            var cell = ws.Cell(r, column);
            var text = (cell.IsMerged() ? cell.MergedRange().FirstCell() : cell).GetString().Trim();
            if (text.Length > 0 && (parts.Count == 0 || parts[^1] != text))
            {
                parts.Add(text);
            }
        }

        return string.Join(" / ", parts);
    }

    /// <summary>Кількість знаків після коми з формату числа: <c>0.000</c> → 3, <c>#,##0</c> → 0.</summary>
    /// <param name="format">Формат Excel.</param>
    /// <returns>Масштаб або <c>null</c>, якщо формат його не задає.</returns>
    public static byte? ScaleOf(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || format.Equals("General", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var section = format.Split(';')[0];
        var match = NumericFormat().Match(section);
        if (!match.Success)
        {
            return null;
        }

        return (byte)match.Groups["frac"].Value.Length;
    }

    private static bool IsDateFormat(IXLCell cell)
    {
        var id = cell.Style.NumberFormat.NumberFormatId;
        if (id is >= 14 and <= 22)
        {
            return true;
        }

        var format = cell.Style.NumberFormat.Format;
        return !string.IsNullOrEmpty(format) && DateFormat().IsMatch(format);
    }

    private static bool IsMonth(string header)
    {
        var h = header.Trim().ToLowerInvariant();
        return h.Length >= 3 && MonthNames.Any(m => h.StartsWith(m, StringComparison.Ordinal));
    }

    [GeneratedRegex(@"^(№|n|no|nr|#|номер|п/п|№\s*п/п)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberingHeader();

    [GeneratedRegex(@"^(всього|усього|разом|итого|всего|total)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TotalLabel();

    [GeneratedRegex(@"^[^0#\.]*[#,]*0+(?:\.(?<frac>0*)#*)?")]
    private static partial Regex NumericFormat();

    [GeneratedRegex(@"(^|[^\\""])(d{1,4}|m{1,4}|y{2,4})", RegexOptions.IgnoreCase)]
    private static partial Regex DateFormat();
}
