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

    /// <summary>Порожній перелік рядків — для таблиці, у якої їх немає.</summary>
    private static readonly IReadOnlyDictionary<string, long> NoRows =
        new Dictionary<string, long>(StringComparer.Ordinal);

    /// <summary>Порожній зріз — для таблиці, у якої немає непорожніх комірок.</summary>
    private static readonly IReadOnlyList<CellRecord> NoCells = [];

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
                $"Документа {documentId} за період {options.PeriodKey} не існує або він порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.periodEmpty",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["periodKey"] = options.PeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var snapshot = await metadata
            .GetAsync(instances[0].TemplateVersionId, ct)
            .ConfigureAwait(false);

        var styleMap = options.IncludeStyles
            ? await styles.GetAsync(snapshot.TemplateVersionId, ct).ConfigureAwait(false)
            : new Dictionary<int, StyleDef>();

        var lookups = await LookupsAsync(snapshot, ct).ConfigureAwait(false);
        var byTableDef = instances.ToDictionary(i => i.TableDefId);

        // ⛔ Рядки й комірки ВСІХ таблиць читаються ДВОМА пакетними запитами
        // до циклу, а не двома запитами на кожну з ~90 таблиць документа
        // (директива №14 §3.6). Пакетні методи для цього вже є в портах — їх
        // завела Q-165/Q-168 по сліду того самого дефекту в перегляді імпорту;
        // експорт лишався єдиним місцем, яке ними не користувалося.
        //
        // ⚠ Період один на весь набір: `GetTableInstancesAsync` вище вже
        // відібрав екземпляри саме за `periodKey`, тож `instance.PeriodKey`
        // кожного з них дорівнює йому — окремий ключ на таблицю був би тим
        // самим числом, поданим як різне.
        var instanceIds = instances.Select(i => i.TableInstanceId).ToList();

        var rowIdsBatch = await rowStore
            .GetRowIdsBatchAsync(instanceIds, periodKey, ct)
            .ConfigureAwait(false);

        var slicesBatch = await cellStore
            .ReadSlicesAsync(instanceIds, ct)
            .ConfigureAwait(false);

        using var workbook = new XLWorkbook();

        // Носій окремих значень стилю: ClosedXML не дає створити `IXLStyle`
        // поза книгою (`XLStyle` — internal), а стиль, узятий у самої книги
        // (`worksheet.Style`), — це ЖИВИЙ проксі на її типовий стиль, і
        // правка такого «зразка» мовчки перефарбовує весь аркуш. Тому зразки
        // живуть в окремій книзі, яку ніхто не зберігає.
        using var styleSource = new StyleSource();

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
            var headerRows = new List<int>(tables.Count);
            var lastColumn = 0;

            foreach (var table in tables)
            {
                var instance = byTableDef[table.Id];

                var block = WriteTable(
                    worksheet, name, table, instance, snapshot, styleMap, lookups, styleSource, options, row,
                    rowIdsBatch.GetValueOrDefault(instance.TableInstanceId, NoRows),
                    slicesBatch.GetValueOrDefault(instance.TableInstanceId, NoCells));

                blocks.Add(block);
                headerRows.Add(block.HeaderRow);
                lastColumn = Math.Max(lastColumn, block.Columns.Count);
                row = block.HeaderRow + block.Rows.Count + 1 + GapRows;
            }

            AdjustHeaders(worksheet, headerRows, lastColumn);

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
        //
        // ⛔ І саме тому це ФАЙЛ, а не `MemoryStream`. До директиви №14 §3.6
        // коментар вище вже обіцяв «потік», а код поруч робив рівно те, чого
        // коментар застерігався: тримав повний вміст книги в пам'яті ДРУГОЮ
        // копією — поряд із об'єктною моделлю ClosedXML, яка й так важить
        // сотні мегабайтів на документі 91 × 500 × 60.
        //
        // ⚠ `DeleteOnClose`: файл зникає, щойно споживач закриє потік
        // (`ExcelExportJob` бере його через `await using`). Прибирання за
        // розкладом не потрібне — його не треба писати й не можна забути.
        var output = TemporaryFile();

        try
        {
            workbook.SaveAs(output);
            output.Position = 0;
        }
        catch
        {
            await output.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return output;
    }

    /// <summary>Порожній файл, який видаляє сам себе при закритті потоку.</summary>
    private static FileStream TemporaryFile()
        => new(
            Path.Combine(Path.GetTempPath(), $"ecr-export-{Guid.NewGuid():N}.xlsx"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

    /// <summary>
    /// Підганяє ширину колонок під РЯДКИ ЗАГОЛОВКІВ, а не під увесь аркуш.
    /// </summary>
    /// <remarks>
    /// ⛔ До директиви №14 §3.6 тут стояло <c>Columns().AdjustToContents()</c>
    /// по всьому аркушу: ClosedXML міряє ширину ТЕКСТУ кожної комірки, тобто
    /// на 500 × 60 це тридцять тисяч вимірювань шрифту на таблицю (виміряно:
    /// 482 мс проти 5 мс на рядок заголовків).
    ///
    /// ⚠ Це ВИДИМА зміна, і назвати її чесніше, ніж замовчати: колонка з
    /// довгими значеннями тепер не розсувається під них. Підписи колонок
    /// видно повністю, довге значення — під обріз, як і в будь-якій книзі, де
    /// ширину не чіпали.
    ///
    /// ⚠ Аркуш несе кілька таблиць із РІЗНИМИ заголовками, а
    /// <c>AdjustToContents</c> приймає один діапазон рядків і кожним викликом
    /// ПЕРЕЗАПИСУЄ ширину. Тому максимум по рядках заголовків береться тут
    /// самостійно — інакше остання таблиця аркуша обрізала б заголовки всіх
    /// попередніх.
    /// </remarks>
    private static void AdjustHeaders(IXLWorksheet worksheet, List<int> headerRows, int lastColumn)
    {
        if (headerRows.Count == 0 || lastColumn == 0)
        {
            return;
        }

        var widths = new double[lastColumn + 1];

        foreach (var header in headerRows)
        {
            worksheet.Columns(1, lastColumn).AdjustToContents(header, header);

            for (var number = 1; number <= lastColumn; number++)
            {
                widths[number] = Math.Max(widths[number], worksheet.Column(number).Width);
            }
        }

        for (var number = 1; number <= lastColumn; number++)
        {
            worksheet.Column(number).Width = widths[number];
        }
    }

    /// <summary>Пише одну таблицю і повертає її блок у карті книги.</summary>
    /// <remarks>
    /// ⚠ Метод СИНХРОННИЙ і рядки з комірками приймає готовими. До директиви
    /// №14 §3.6 він сам ходив у базу двічі — і робив це на кожну з ~90 таблиць
    /// документа.
    /// </remarks>
    private ExcelTableBlock WriteTable(
        IXLWorksheet worksheet,
        string sheetName,
        TableDef table,
        TableInstanceRef instance,
        TemplateVersionSnapshot snapshot,
        IReadOnlyDictionary<int, StyleDef> styleMap,
        IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups,
        StyleSource styleSource,
        ExcelExportOptions options,
        int startRow,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlyList<CellRecord> cells)
    {
        var columns = table.Columns
            .Where(c => !c.IsDeleted && !c.IsHidden)
            .OrderBy(c => c.Ordinal)
            .ToList();

        var titleRow = startRow;
        worksheet.Cell(titleRow, 1).Value = table.NameL10n.Get(options.Language) ?? table.Code;
        worksheet.Cell(titleRow, 1).Style.Font.Bold = true;

        var headerRow = titleRow + 1;
        var columnRefs = new List<ExcelColumnRef>(columns.Count);
        var columnNumbers = new Dictionary<int, int>(columns.Count);

        // Стиль заголовка — один на всю таблицю (обидві його складові,
        // `MarkHeader` і `HeaderStyleId`, належать ТАБЛИЦІ, не колонці), тож
        // будується раз і лягає на весь рядок заголовків одним присвоєнням.
        var headerStyleValue = styleSource.Fresh();
        styleMapper.MarkHeader(headerStyleValue);

        if (options.IncludeStyles && table.HeaderStyleId is { } headerStyle)
        {
            styleMapper.Apply(headerStyleValue, styleMap.GetValueOrDefault(headerStyle));
        }

        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];

            worksheet.Cell(headerRow, i + 1).Value =
                column.HeaderL10n.Get(options.Language) ?? column.Code;

            columnNumbers[column.Id] = i + 1;

            columnRefs.Add(new ExcelColumnRef(
                column.Id,
                column.Code,
                i + 1,
                IsCalculated(column),
                column.LookupRegistryDefId));
        }

        if (columns.Count > 0)
        {
            worksheet.Range(headerRow, 1, headerRow, columns.Count).Style = headerStyleValue;
        }

        // Порядок рядків — за описом шаблону, а рядки без опису — у порядку
        // появи (ідентифікатор рядка). Порядок «як прийшло з бази» змінювався б
        // від запуску до запуску, і diff двох вивантажень показував би зміни там,
        // де їх немає.
        //
        // ⛔ `V-10`: це ТЕ САМЕ правило, що в сітці (`GetTableSliceHandler`:
        // `Ordinal`, потім `RowId`). Доти рядки без опису йшли за ключем
        // ОРДИНАЛЬНО — `R1, R10, …, R18, R2` — і книга не збігалася з екраном, з
        // якого її вивантажили.
        var keys = rowIds.Keys
            .OrderBy(k => snapshot.RowsByKey.TryGetValue((table.Id, k), out var def) ? def.Ordinal : int.MaxValue)
            .ThenBy(k => rowIds[k])
            .ToList();

        var rowRefs = new List<ExcelRowRef>(keys.Count);
        var rowNumbers = new Dictionary<string, int>(keys.Count, StringComparer.Ordinal);

        for (var r = 0; r < keys.Count; r++)
        {
            var number = headerRow + 1 + r;
            rowRefs.Add(new ExcelRowRef(keys[r], number));
            rowNumbers[keys[r]] = number;
        }

        StyleDataColumns(worksheet, columns, styleMap, styleSource, options, headerRow, keys.Count);
        WriteValues(worksheet, cells, rowIds, columns, columnNumbers, rowNumbers, lookups);

        return new ExcelTableBlock(
            instance.TableInstanceId, table.Id, table.Code, sheetName, headerRow, columnRefs, rowRefs);
    }

    /// <summary>
    /// Кладе стиль колонки на ВЕСЬ її стовпчик даних одним присвоєнням.
    /// </summary>
    /// <remarks>
    /// ⛔ До директиви №14 §3.6 ці самі чотири виклики <c>StyleMapper</c>
    /// робилися на КОЖНУ комірку таблиці, зокрема на порожню (<c>continue</c>
    /// стояв ПІСЛЯ них). Усі три складові стилю — <c>StyleId</c>,
    /// <c>DisplayFormat</c>/<c>Scale</c> і ознака обчисленої — належать
    /// КОЛОНЦІ, тобто на 500 рядків та сама робота повторювалася 500 разів.
    /// Виміряно на 500 × 60: 530 мс проти 47 мс.
    ///
    /// ⚠ Стиль лягає на ДІАПАЗОН, а не на стовпець аркуша
    /// (<c>worksheet.Column(n).Style</c>), хоча той був би ще вчетверо
    /// дешевшим. Аркуш несе кілька таблиць одна під одною, і стовпець аркуша
    /// перетинає їх усі: стиль стовпця пофарбував би і чужі таблиці, і
    /// порожнечу під останньою — до самого низу аркуша. Виміряно й перевірено
    /// на порожній комірці поза діапазоном.
    ///
    /// ⚠ Порожні комірки діапазону лишаються оформленими — так було й до
    /// зміни. Обчислена колонка, у якій ще нічого не ввели, мусить бути сірою:
    /// саме це попереджає користувача, що правка буде відхилена.
    /// </remarks>
    private void StyleDataColumns(
        IXLWorksheet worksheet,
        List<ColumnDef> columns,
        IReadOnlyDictionary<int, StyleDef> styleMap,
        StyleSource styleSource,
        ExcelExportOptions options,
        int headerRow,
        int rowCount)
    {
        if (rowCount == 0)
        {
            return;
        }

        var firstRow = headerRow + 1;
        var lastRow = headerRow + rowCount;

        for (var c = 0; c < columns.Count; c++)
        {
            var column = columns[c];
            var style = styleSource.Fresh();

            if (options.IncludeStyles && column.StyleId is { } styleId)
            {
                styleMapper.Apply(style, styleMap.GetValueOrDefault(styleId));
            }

            styleMapper.ApplyNumberFormat(style, column.DisplayFormat, column.Scale);

            if (IsCalculated(column))
            {
                // ⚠ Обчислена комірка позначається ще до того, як у неї
                // щось запишуть: при зворотному імпорті правку буде
                // відхилено, і без позначки це виглядало б як втрата роботи.
                styleMapper.MarkCalculated(style);
            }

            worksheet.Range(firstRow, c + 1, lastRow, c + 1).Style = style;
        }
    }

    /// <summary>Пише значення — і лише їх.</summary>
    /// <remarks>
    /// ⛔ Обхід іде по ЗРІЗУ, а не по сітці «рядки × колонки». Зріз порожніх
    /// комірок не повертає (ФВ-3.8), тож на документі, заповненому на десяту
    /// частину, це десята частина роботи. Обхід сіткою робив би
    /// <c>worksheet.Cell(r, c)</c> для кожної комірки — тобто матеріалізував
    /// би її в книзі — і лише потім з'ясовував, що писати нема чого.
    ///
    /// ⚠ Комірки рядків, яких немає в переліку ключів, ВІДКИДАЮТЬСЯ (перевірка
    /// <c>rowKeys</c>), як і комірки прихованих та видалених колонок
    /// (перевірка <c>columnNumbers</c>) — обидві множини звужені навмисно, і
    /// мовчазний пропуск тут правильний: ці комірки не мають місця в книзі.
    /// </remarks>
    private static void WriteValues(
        IXLWorksheet worksheet,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds,
        List<ColumnDef> columns,
        Dictionary<int, int> columnNumbers,
        Dictionary<string, int> rowNumbers,
        IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups)
    {
        if (cells.Count == 0)
        {
            return;
        }

        var byRowId = new Dictionary<long, string>(rowIds.Count);
        foreach (var (key, id) in rowIds)
        {
            byRowId[id] = key;
        }

        var byColumnId = new Dictionary<int, ColumnDef>(columns.Count);
        foreach (var column in columns)
        {
            byColumnId[column.Id] = column;
        }

        foreach (var cell in cells)
        {
            if (!byRowId.TryGetValue(cell.Address.TableRowId, out var rowKey)
                || !rowNumbers.TryGetValue(rowKey, out var number)
                || !columnNumbers.TryGetValue(cell.Address.ColumnDefId, out var columnNumber))
            {
                continue;
            }

            WriteValue(
                worksheet.Cell(number, columnNumber),
                byColumnId[cell.Address.ColumnDefId],
                cell.Value,
                lookups);
        }
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
    ///
    /// ⛔ `V-10`: формула лягає рівно туди, де її рахує система
    /// (<c>RecalculationService.Targets</c>), і транслюється ОКРЕМО для кожної
    /// комірки — з таблицею й рядком цієї комірки: <c>[A] * 2</c> у рядку 7 — це
    /// <c>A7*2</c>, а не <c>#REF!*2</c>. Доти формула колонки транслювалася один
    /// раз без контексту і клалася лише в ПЕРШИЙ рядок.
    ///
    /// ⚠ Рішення для того, що відтворити не можна (посилання на інший період,
    /// предикат, діалект методології, невідома функція): комірка лишається
    /// ЗНАЧЕННЯМ, без формули. Excel не показує <c>#REF!</c> там, де в системі
    /// число, а імпорт однаково не бере з обчислюваної комірки нічого. Так само —
    /// для комірки, на яку претендують ДВІ формули (колонки й рядка): яка з них
    /// правильна, вирішує перерахунок, а не книга.
    ///
    /// ⚠ Формула рядка БЕЗ колонки («усі колонки рядка») у книгу не пишеться:
    /// вона лягла б і в текстові колонки, і в дати.
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
            var targets = new Dictionary<(string RowKey, int ColumnDefId), List<FormulaDef>>();

            foreach (var formula in table.Formulas.Where(f => !f.IsDeleted && f.ColumnDefId is not null))
            {
                foreach (var row in Rows(table, block, formula))
                {
                    var key = (row.RowKey, formula.ColumnDefId!.Value);

                    if (!targets.TryGetValue(key, out var list))
                    {
                        targets[key] = list = [];
                    }

                    list.Add(formula);
                }
            }

            var rowNumbers = block.Rows.ToDictionary(r => r.RowKey, r => r.Number, StringComparer.Ordinal);

            foreach (var ((rowKey, columnDefId), formulas) in targets)
            {
                var column = block.Columns.FirstOrDefault(c => c.ColumnDefId == columnDefId);

                if (column is null || formulas.Count != 1)
                {
                    continue;
                }

                var translated = formulaTranslator.ToExcel(
                    formulas[0].Expression, coordinates, new FormulaContext(table.Code, rowKey));

                if (string.IsNullOrEmpty(translated) || FormulaTranslator.IsBroken(translated))
                {
                    continue;
                }

                worksheet.Cell(rowNumbers[rowKey], column.Number).FormulaA1 = translated;
            }
        }
    }

    /// <summary>Рядки блоку, які обчислює формула, — те саме правило, що в перерахунку.</summary>
    private static IEnumerable<ExcelRowRef> Rows(TableDef table, ExcelTableBlock block, FormulaDef formula)
    {
        if (formula.Scope == FormulaScope.Column)
        {
            return block.Rows;
        }

        var rowKey = Ecr.Application.Recalculation.FormulaOutputs.RowKeyOf(table, formula);

        return rowKey is null
            ? []
            : block.Rows.Where(r => string.Equals(r.RowKey, rowKey, StringComparison.Ordinal));
    }

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

    /// <summary>
    /// Постачальник ОКРЕМИХ значень стилю для присвоєння діапазону.
    /// </summary>
    /// <remarks>
    /// ⛔ ClosedXML не дає створити <c>IXLStyle</c> поза книгою: <c>XLStyle</c>
    /// у пакеті <c>internal</c>. Єдине, що лишається, — узяти стиль у комірки;
    /// а стиль, узятий у книги або аркуша (<c>worksheet.Style</c>), — це ЖИВИЙ
    /// проксі на їхній ТИПОВИЙ стиль, і правка такого «зразка» перефарбовує
    /// весь аркуш мовчки. Цей дефект уже один раз зробив замір недійсним:
    /// варіант «зібрати стиль на <c>worksheet.Style</c>» показав 5 мс проти
    /// 530 мс саме тому, що не робив жодної роботи.
    ///
    /// ⚠ Кожен зразок — НОВА комірка допоміжної книги, а не очищена стара.
    /// Очищення давало б ще один спосіб мовчки перенести стиль однієї колонки
    /// на іншу; нових комірок тут кілька тисяч на весь експорт, і книга ця
    /// ніколи не зберігається.
    /// </remarks>
    private sealed class StyleSource : IDisposable
    {
        private readonly XLWorkbook _workbook = new();
        private readonly IXLWorksheet _sheet;
        private int _row;

        public StyleSource() => _sheet = _workbook.Worksheets.Add("s");

        /// <summary>Чистий стиль за замовчуванням, який можна вільно міняти.</summary>
        public IXLStyle Fresh() => _sheet.Cell(++_row, 1).Style;

        public void Dispose() => _workbook.Dispose();
    }
}
