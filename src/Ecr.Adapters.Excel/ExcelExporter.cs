using System.Globalization;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Експорт документа в <c>.xlsx</c> (ClosedXML, MIT).
/// </summary>
/// <remarks>
/// Бюджет — 10 с p95, тому операція фонова, з прогресом (tz/08 §8.2).
/// ⛔ EPPlus 5+ заборонений ліцензійно (noncommercial), тому альтернативи тут
/// немає і шукати її не треба.
/// </remarks>
public sealed class ExcelExporter(
    ICellStore cellStore,
    IRowStore rowStore,
    IMetadataCache metadata,
    IStyleCatalog styles,
    IRegistryStore registries,
    StyleMapper styleMapper,
    FormulaTranslator formulaTranslator) : IExcelExporter
{
    /// <summary>Рядок, з якого починається перший блок аркуша.</summary>
    private const int FirstRow = 1;

    /// <summary>Скільки порожніх рядків між таблицями одного аркуша.</summary>
    private const int GapRows = 2;

    /// <summary>Гранична довжина імені аркуша в Excel.</summary>
    private const int MaxSheetNameLength = 31;

    /// <summary>Символи, заборонені в імені аркуша.</summary>
    private static readonly char[] ForbiddenInSheetName = ['[', ']', ':', '*', '?', '/', '\\'];

    /// <summary>Налаштування карти книги; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions MapOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<Stream> ExportAsync(long documentId, ExcelExportOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var periodKey = new PeriodKey(options.PeriodKey);

        var instances = await rowStore
            .GetTableInstancesAsync(documentId, periodKey, ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документа {documentId} за період {options.PeriodKey} не існує або він порожній.");
        }

        var snapshot = await metadata
            .GetAsync(instances[0].TemplateVersionId, ct)
            .ConfigureAwait(false);

        var styleMap = options.IncludeStyles
            ? await styles.GetAsync(snapshot.TemplateVersionId, ct).ConfigureAwait(false)
            : new Dictionary<int, StyleDef>();

        var lookups = await LookupsAsync(snapshot, ct).ConfigureAwait(false);
        var byTableDef = instances.ToDictionary(i => i.TableDefId);

        using var workbook = new XLWorkbook();
        var blocks = new List<ExcelTableBlock>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted).OrderBy(s => s.Ordinal))
        {
            var tables = sheet.Tables
                .Where(t => !t.IsDeleted && byTableDef.ContainsKey(t.Id))
                .OrderBy(t => t.Ordinal)
                .ToList();

            if (tables.Count == 0)
            {
                continue;
            }

            var name = SheetName(sheet, options.Language, usedNames);
            var worksheet = workbook.Worksheets.Add(name);
            var row = FirstRow;

            foreach (var table in tables)
            {
                var block = await WriteTableAsync(
                    worksheet, name, table, byTableDef[table.Id], snapshot, styleMap, lookups,
                    options, row, ct).ConfigureAwait(false);

                blocks.Add(block);
                row = block.HeaderRow + block.Rows.Count + 1 + GapRows;
            }

            worksheet.Columns().AdjustToContents();

            // ⚠ Заморожується рядок ЗАГОЛОВКА першої таблиці, а не перший
            // рядок аркуша: у першому лежить назва таблиці, і замороження по
            // ньому лишало б видимою назву, а не підписи колонок — тобто саме
            // те, заради чого це роблять.
            var firstHeader = blocks.FirstOrDefault(b => b.SheetName == name)?.HeaderRow ?? FirstRow;
            worksheet.SheetView.FreezeRows(firstHeader);
        }

        if (options.IncludeFormulas)
        {
            WriteFormulas(workbook, snapshot, blocks);
        }

        WriteMap(workbook, new ExcelWorkbookMap(
            documentId, options.PeriodKey, snapshot.TemplateVersionId, blocks));

        // ⚠ Віддається Stream, а не байти: документ 500×60×12 у пам'яті — це
        // десятки МБ на кожен паралельний експорт, і саме вони кладуть процес
        // в останній день періоду, коли експортують усі одразу.
        var output = new MemoryStream();
        workbook.SaveAs(output);
        output.Position = 0;

        return output;
    }

    /// <summary>Пише одну таблицю і повертає її блок у карті книги.</summary>
    private async Task<ExcelTableBlock> WriteTableAsync(
        IXLWorksheet worksheet,
        string sheetName,
        TableDef table,
        TableInstanceRef instance,
        TemplateVersionSnapshot snapshot,
        IReadOnlyDictionary<int, StyleDef> styleMap,
        IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups,
        ExcelExportOptions options,
        int startRow,
        CancellationToken ct)
    {
        var periodKey = new PeriodKey(instance.PeriodKey);

        var rowIds = await rowStore
            .GetRowIdsAsync(instance.TableInstanceId, periodKey, ct)
            .ConfigureAwait(false);

        var cells = await cellStore
            .ReadSliceAsync(instance.TableInstanceId, ct)
            .ConfigureAwait(false);

        var columns = table.Columns
            .Where(c => !c.IsDeleted && !c.IsHidden)
            .OrderBy(c => c.Ordinal)
            .ToList();

        var titleRow = startRow;
        worksheet.Cell(titleRow, 1).Value = table.NameL10n.Get(options.Language) ?? table.Code;
        worksheet.Cell(titleRow, 1).Style.Font.Bold = true;

        var headerRow = titleRow + 1;
        var columnRefs = new List<ExcelColumnRef>(columns.Count);

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var cell = worksheet.Cell(headerRow, i + 1);

            cell.Value = column.HeaderL10n.Get(options.Language) ?? column.Code;
            styleMapper.MarkHeader(cell.Style);

            if (options.IncludeStyles && table.HeaderStyleId is { } headerStyle)
            {
                styleMapper.Apply(cell.Style, styleMap.GetValueOrDefault(headerStyle));
            }

            columnRefs.Add(new ExcelColumnRef(
                column.Id,
                column.Code,
                i + 1,
                IsCalculated(column),
                column.LookupRegistryDefId));
        }

        // Порядок рядків — за описом шаблону, а динамічні — за ключем. Порядок
        // «як прийшло з бази» змінювався б від запуску до запуску, і diff двох
        // вивантажень показував би зміни там, де їх немає.
        var keys = rowIds.Keys
            .OrderBy(k => snapshot.RowsByKey.TryGetValue((table.Id, k), out var def) ? def.Ordinal : int.MaxValue)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();

        var byRowId = rowIds.ToDictionary(p => p.Value, p => p.Key);

        // ⚠ Комірки рядків, яких немає в переліку ключів, ВІДКИДАЮТЬСЯ, а не
        // зводяться до спільного ключа з порожнім RowKey. Інакше дві такі
        // комірки давали б однаковий ключ — і `ToDictionary` падав би на
        // дублікаті посеред експорту, на даних, які виглядають звичайними.
        var values = cells
            .Where(c => byRowId.ContainsKey(c.Address.TableRowId))
            .ToDictionary(c => (byRowId[c.Address.TableRowId], c.Address.ColumnDefId));

        var rowRefs = new List<ExcelRowRef>(keys.Count);

        for (var r = 0; r < keys.Count; r++)
        {
            var number = headerRow + 1 + r;
            rowRefs.Add(new ExcelRowRef(keys[r], number));

            for (var c = 0; c < columns.Count; c++)
            {
                var column = columns[c];
                var cell = worksheet.Cell(number, c + 1);

                if (options.IncludeStyles && column.StyleId is { } styleId)
                {
                    styleMapper.Apply(cell.Style, styleMap.GetValueOrDefault(styleId));
                }

                styleMapper.ApplyNumberFormat(cell.Style, column.DisplayFormat, column.Scale);

                if (IsCalculated(column))
                {
                    // ⚠ Обчислена комірка позначається ще до того, як у неї
                    // щось запишуть: при зворотному імпорті правку буде
                    // відхилено, і без позначки це виглядало б як втрата роботи.
                    styleMapper.MarkCalculated(cell.Style);
                }

                if (!values.TryGetValue((keys[r], column.Id), out var record))
                {
                    // ⛔ Порожні комірки не пишуться взагалі. Зріз їх і не
                    // повертає (ФВ-3.8), а вписаний нуль став би значенням,
                    // якого користувач не вводив.
                    continue;
                }

                WriteValue(cell, column, record.Value, lookups);
            }
        }

        return new ExcelTableBlock(
            instance.TableInstanceId, table.Id, table.Code, sheetName, headerRow, columnRefs, rowRefs);
    }

    /// <summary>Кладе значення комірки за її типом.</summary>
    /// <remarks>
    /// ⚠ Тип береться з <c>ColumnDef</c>, а не «як лежить». Число, записане
    /// текстом, в Excel не підсумовується, а дата, записана числом, показує
    /// 45 000 — обидва випадки виглядають як зіпсовані дані, хоча дані цілі.
    /// </remarks>
    private static void WriteValue(
        IXLCell cell,
        ColumnDef column,
        CellValueData value,
        IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups)
    {
        if (value.IsEmpty)
        {
            return;
        }

        switch (column.DataType)
        {
            case CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated:
                if (value.ValueNumeric is { } number)
                {
                    cell.Value = number;
                }

                break;

            case CellDataType.Bool:
                if (value.ValueBool is { } flag)
                {
                    cell.Value = flag;
                }

                break;

            case CellDataType.Date:
                if (value.ValueDate is { } date)
                {
                    cell.Value = date;
                }

                break;

            case CellDataType.Lookup:
                // ⚠ У книгу йде КОД запису довідника, а не його ідентифікатор.
                // Ідентифікатор нічого не означає для людини і не переживає
                // перенесення між середовищами; код — переживає.
                if (value.ValueRegistryEntryId is { } entryId
                    && column.LookupRegistryDefId is { } registryId
                    && lookups.TryGetValue(registryId, out var entries)
                    && entries.TryGetValue(entryId, out var code))
                {
                    cell.Value = code;
                }
                else if (value.ValueRegistryEntryId is { } orphan)
                {
                    cell.Value = orphan.ToString(CultureInfo.InvariantCulture);
                }

                break;

            case CellDataType.Unit:
                if (value.ValueUnitId is { } unitId)
                {
                    cell.Value = unitId;
                }

                break;

            default:
                if (value.ValueString is { } text)
                {
                    cell.Value = text;
                }

                break;
        }
    }

    /// <summary>Пише формули другим проходом, коли всі координати вже відомі.</summary>
    /// <remarks>
    /// ⚠ Саме другим проходом. Формула може посилатися на таблицю, яку ще не
    /// вивантажили; трансляція під час першого проходу давала б
    /// <c>#REF!</c> залежно від порядку аркушів — тобто відтворювано неправильно.
    /// </remarks>
    private void WriteFormulas(
        XLWorkbook workbook, TemplateVersionSnapshot snapshot, IReadOnlyList<ExcelTableBlock> blocks)
    {
        var coordinates = Coordinates(blocks);
        var byTableDef = blocks.ToDictionary(b => b.TableDefId);

        foreach (var table in snapshot.Sheets.SelectMany(s => s.Tables).Where(t => !t.IsDeleted))
        {
            if (!byTableDef.TryGetValue(table.Id, out var block))
            {
                continue;
            }

            var worksheet = workbook.Worksheet(block.SheetName);

            foreach (var formula in table.Formulas.Where(f => !f.IsDeleted && f.ColumnDefId is not null))
            {
                var column = block.Columns.FirstOrDefault(c => c.ColumnDefId == formula.ColumnDefId);

                if (column is null)
                {
                    continue;
                }

                var translated = formulaTranslator.ToExcel(formula.Expression, coordinates);

                if (string.IsNullOrEmpty(translated))
                {
                    continue;
                }

                foreach (var row in Rows(block, formula))
                {
                    worksheet.Cell(row.Number, column.Number).FormulaA1 = translated;
                }
            }
        }
    }

    /// <summary>Рядки, на які лягає формула.</summary>
    private static IEnumerable<ExcelRowRef> Rows(ExcelTableBlock block, FormulaDef formula)
        => formula.Scope == FormulaScope.Row
            ? block.Rows
            : block.Rows.Take(1);

    /// <summary>Мапа <c>(TableCode, RowKey, ColumnCode)</c> → адреса Excel.</summary>
    private static Dictionary<(string, string, string), string> Coordinates(
        IReadOnlyList<ExcelTableBlock> blocks)
    {
        var map = new Dictionary<(string, string, string), string>();

        foreach (var block in blocks)
        {
            foreach (var column in block.Columns)
            {
                var letter = XLHelper.GetColumnLetterFromNumber(column.Number);

                foreach (var row in block.Rows)
                {
                    // Ім'я аркуша в посиланні — в апострофах: коди аркушів
                    // бувають із пробілами, і без них Excel читає формулу до
                    // першого пробілу.
                    map[(block.TableCode, row.RowKey, column.Code)] =
                        $"'{block.SheetName}'!{letter}{row.Number.ToString(CultureInfo.InvariantCulture)}";
                }
            }
        }

        return map;
    }

    /// <summary>Коди записів довідників, використаних колонками підстановки.</summary>
    /// <remarks>
    /// По довіднику, а не по комірці: у таблиці на 500 рядків та сама
    /// підстановка зустрічається 500 разів, і запит на кожну означав би 500
    /// звернень заради двох десятків кодів.
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>>> LookupsAsync(
        TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var registryIds = snapshot.ColumnsById.Values
            .Where(c => !c.IsDeleted && c.LookupRegistryDefId is not null)
            .Select(c => c.LookupRegistryDefId!.Value)
            .Distinct()
            .ToList();

        var result = new Dictionary<int, IReadOnlyDictionary<long, string>>();

        foreach (var registryId in registryIds)
        {
            var entries = await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false);

            result[registryId] = entries
                .GroupBy(e => e.Id)
                .ToDictionary(g => g.Key, g => g.First().Code);
        }

        return result;
    }

    /// <summary>
    /// Кладе карту книги в прихований аркуш.
    /// </summary>
    /// <remarks>
    /// ⛔ Карта пишеться ШМАТКАМИ по рядках: у комірку Excel не влазить більше
    /// за 32 767 символів, а карта реального документа — це сотні кілобайт
    /// (`A7-29`). Одне значення падало на кожному несинтетичному документі,
    /// причому вже після побудови всієї книги.
    ///
    /// ⚠ Шматки йдуть у стовпець A по одному на рядок і збираються назад
    /// простою склейкою. Ділити JSON по рядках-полях було б охайніше на
    /// вигляд і крихкіше по суті: будь-яка зміна форми карти зламала б
    /// зчитування старих книг.
    /// </remarks>
    private static void WriteMap(XLWorkbook workbook, ExcelWorkbookMap map)
    {
        var sheet = workbook.Worksheets.Add(ExcelWorkbookMap.SheetName);
        var json = JsonSerializer.Serialize(map, MapOptions);

        for (var offset = 0; offset < json.Length; offset += ExcelWorkbookMap.ChunkSize)
        {
            var length = Math.Min(ExcelWorkbookMap.ChunkSize, json.Length - offset);

            sheet.Cell((offset / ExcelWorkbookMap.ChunkSize) + 1, 1).Value =
                json.Substring(offset, length);
        }

        sheet.Hide();
    }

    /// <summary>Чи рахує комірки цієї колонки система.</summary>
    private static bool IsCalculated(ColumnDef column)
        => column.DataType is CellDataType.Formula or CellDataType.Calculated || column.IsReadOnly;

    /// <summary>Ім'я аркуша: коротке, без заборонених символів і унікальне.</summary>
    /// <remarks>
    /// ⚠ Excel мовчки відмовляється створювати аркуш із задовгим або
    /// повторним іменем. Різати й розводити імена треба тут, інакше експорт
    /// падає на книзі, у якій два аркуші починаються однаково — а в реальних
    /// шаблонах вони так і починаються.
    /// </remarks>
    private static string SheetName(SheetDef sheet, string language, HashSet<string> used)
    {
        var name = sheet.NameL10n.Get(language) ?? sheet.Code;

        foreach (var symbol in ForbiddenInSheetName)
        {
            name = name.Replace(symbol, ' ');
        }

        name = name.Trim();

        if (name.Length == 0)
        {
            name = sheet.Code;
        }

        if (name.Length > MaxSheetNameLength)
        {
            name = name[..MaxSheetNameLength];
        }

        var candidate = name;
        var suffix = 2;

        while (!used.Add(candidate))
        {
            var tail = $"~{suffix.ToString(CultureInfo.InvariantCulture)}";
            candidate = name.Length + tail.Length > MaxSheetNameLength
                ? name[..(MaxSheetNameLength - tail.Length)] + tail
                : name + tail;

            suffix++;
        }

        return candidate;
    }
}
