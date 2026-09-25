using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Формати вивантаження документа (ФВ-4.1, ФВ-4.2).</summary>
/// <remarks>
/// ⚠ Тип вмісту при завантаженні визначається за САМИМ вмістом: сховище експорту
/// пам'ятає лише байти й документ, а <c>exportId</c> лишається 32 hex-символами
/// для всіх форматів — на цю форму спирається <c>resultUrl</c> задачі (UX-09).
/// </remarks>
public static class DocumentExportFormat
{
    public const string Xlsx = "xlsx";
    public const string Csv = "csv";
    public const string Json = "json";

    /// <summary>Нормалізує формат запиту; <c>null</c> — формат невідомий.</summary>
    public static string? Normalize(string? format)
        => (format ?? Xlsx).Trim().ToLowerInvariant() switch
        {
            Xlsx => Xlsx,
            Csv => Csv,
            Json => Json,
            _ => null,
        };

    /// <summary>
    /// Тип вмісту й розширення за вмістом: JSON починається з <c>{</c>; zip без
    /// <c>[Content_Types].xml</c> — архів CSV; решта — книга xlsx.
    /// </summary>
    public static (string ContentType, string Extension) OfContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > 0 && content[0] == (byte)'{')
        {
            return ("application/json", "json");
        }

        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            if (zip.GetEntry("[Content_Types].xml") is null)
            {
                return ("application/zip", "zip");
            }
        }
        catch (InvalidDataException)
        {
            // Не zip — віддаємо як книгу, як і до ФВ-4.2.
        }

        return ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
    }
}

/// <summary>Вивантаження документа за період у CSV (zip, файл на таблицю) або JSON (ФВ-4.2).</summary>
/// <remarks>
/// Та сама семантика значень, що й у <c>ExcelExporter</c>: видимі колонки, порядок
/// рядків за шаблоном, довідник — кодом запису, одиниця — її ідентифікатором.
/// ⛔ Десяткові — рядком в інваріантній культурі, без проходу через <c>double</c>:
/// 16 знаків після коми <c>double</c> не тримає.
/// </remarks>
public sealed class DocumentDataExporter(
    ICellStore cellStore,
    IRowStore rowStore,
    IMetadataCache metadata,
    IRegistryStore registries,
    IMethodologyStore? methodologies = null,
    ICalculationResultStore? results = null)
{
    private const string RowKeyHeader = "rowKey";

    /// <summary>Порожня карта формул — для таблиці без формульних колонок або коли їх не просили.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoFormulas =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Будує файл вивантаження.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період вивантаження.</param>
    /// <param name="format"><c>csv</c> чи <c>json</c> (ФВ-4.2).</param>
    /// <param name="includeFormulas">
    /// Додати формули (ФВ-4.2). На відміну від <c>ExcelExportOptions.IncludeFormulas</c>
    /// у <c>ExcelExporter</c> тут вираз НЕ транслюється в Excel-синтаксис: сітки
    /// клітинок нема, тож координати транслювати нема куди. Вивантажується сирий
    /// текст <c>FormulaDef.Expression</c> мовою редактора виразів проєкту.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<byte[]> ExportAsync(
        long documentId, int periodKey, string format, bool includeFormulas, CancellationToken ct)
    {
        var key = new PeriodKey(periodKey);
        // ⛔ Екземпляри таблиць створюються при ПЕРШОМУ відкритті документа
        // (`GetDocumentTablesHandler`, `A7-30`). Документ, створений і ще не
        // відкритий, їх не має, і вивантаження CSV/JSON відмовляв «документа не існує або він
        // порожній» — хоча документ є і шаблон дає йому таблиці (UX-прохід
        // 2026-09-24, живий стенд). Виклик ідемпотентний.
        await rowStore.EnsureTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);
        var instances = await rowStore.GetTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документа {documentId} за період {periodKey} не існує або він порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.periodEmpty",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                    ["periodKey"] = periodKey.ToString(CultureInfo.InvariantCulture),
                });
        }

        var snapshot = await metadata.GetAsync(instances[0].TemplateVersionId, ct).ConfigureAwait(false);
        var byTableDef = instances.ToDictionary(i => i.TableDefId);
        var ids = instances.Select(i => i.TableInstanceId).ToList();
        var rowIds = await rowStore.GetRowIdsBatchAsync(ids, key, ct).ConfigureAwait(false);
        var slices = await cellStore.ReadSlicesAsync(ids, ct).ConfigureAwait(false);

        // ⛔ F-02: колонка `Calculated` — числом методології, як у сітці й xlsx
        // (`CalculatedCellOverlay`). Порти необов'язкові лише заради тестів.
        if (methodologies is not null && results is not null)
        {
            slices = await new Calculations.CalculatedCellOverlay(methodologies, results)
                .ApplyAsync(documentId, periodKey, snapshot, instances, rowIds, slices, ct)
                .ConfigureAwait(false);
        }
        var lookups = await LookupsAsync(snapshot, ct).ConfigureAwait(false);

        var tables = new List<ExportTable>();
        foreach (var sheet in snapshot.Sheets.Where(s => !s.IsDeleted).OrderBy(s => s.Ordinal))
        {
            foreach (var table in sheet.Tables.Where(t => !t.IsDeleted && byTableDef.ContainsKey(t.Id)).OrderBy(t => t.Ordinal))
            {
                var instanceId = byTableDef[table.Id].TableInstanceId;
                tables.Add(Build(
                    sheet, table, snapshot, lookups,
                    rowIds.GetValueOrDefault(instanceId) ?? new Dictionary<string, long>(),
                    slices.GetValueOrDefault(instanceId) ?? [],
                    includeFormulas));
            }
        }

        return format == DocumentExportFormat.Csv
            ? Zip(tables)
            : Json(documentId, periodKey, snapshot.TemplateVersionId, tables);
    }

    /// <summary>Текст значення комірки; <c>null</c> — порожньо.</summary>
    public static string? FormatValue(
        ColumnDef column, CellValueData value, IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(lookups);
        if (value.IsEmpty)
        {
            return null;
        }

        return column.DataType switch
        {
            _ when IsNumeric(column) => value.ValueNumeric is { } n ? Number(n, column.Scale) : null,
            CellDataType.Bool => value.ValueBool is { } b ? (b ? "true" : "false") : null,
            CellDataType.Date => value.ValueDate?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
            CellDataType.Lookup => value.ValueRegistryEntryId is not { } id
                ? null
                : column.LookupRegistryDefId is { } reg && lookups.TryGetValue(reg, out var e) && e.TryGetValue(id, out var code)
                    ? code
                    : id.ToString(CultureInfo.InvariantCulture),
            CellDataType.Unit => value.ValueUnitId?.ToString(CultureInfo.InvariantCulture),
            _ => value.ValueString,
        };
    }

    private static bool IsNumeric(ColumnDef column)
        => column.DataType is CellDataType.Int or CellDataType.Decimal or CellDataType.Formula or CellDataType.Calculated;

    /// <summary>Число за масштабом колонки — доповнене нулями, але ніколи не округлене.</summary>
    internal static string Number(decimal value, byte? scale)
        => scale is { } s && decimal.Round(value, s) == value
            ? value.ToString("F" + s.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);

    private static ExportTable Build(
        SheetDef sheet,
        TableDef table,
        TemplateVersionSnapshot snapshot,
        IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>> lookups,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlyList<CellRecord> cells,
        bool includeFormulas)
    {
        var columns = table.Columns.Where(c => !c.IsDeleted && !c.IsHidden).OrderBy(c => c.Ordinal).ToList();
        var byColumn = columns.ToDictionary(c => c.Id);
        var keys = rowIds.Keys
            .OrderBy(k => snapshot.RowsByKey.TryGetValue((table.Id, k), out var def) ? def.Ordinal : int.MaxValue)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();
        var keyById = rowIds.ToDictionary(p => p.Value, p => p.Key);
        var values = keys.ToDictionary(k => k, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);

        foreach (var cell in cells)
        {
            if (keyById.TryGetValue(cell.Address.TableRowId, out var rowKey)
                && byColumn.TryGetValue(cell.Address.ColumnDefId, out var column)
                && FormatValue(column, cell.Value, lookups) is { } text)
            {
                values[rowKey][column.Code] = text;
            }
        }

        var formulas = includeFormulas ? ColumnFormulas(table, byColumn) : NoFormulas;

        return new ExportTable(sheet.Code, table.Code, columns, keys.Select(k => (k, values[k])).ToList(), formulas);
    }

    /// <summary>
    /// Сирий вираз (без трансляції) для кожної формульної колонки: код колонки →
    /// <see cref="FormulaDef.Expression"/>.
    /// </summary>
    /// <remarks>
    /// Та сама ідентифікація формульної колонки, що й у
    /// <c>ExcelExporter.WriteFormulas</c>: прив'язка через <c>ColumnDefId</c>, а не
    /// <c>ColumnDef.DataType</c>. ⚠ Ідентичність формули — колонка, не текст
    /// (<c>FormulaDef.SetExpression</c>), тому на <c>Column</c>-область стабільно
    /// припадає щонайбільше один запис; коли на ту саму колонку ще накладається
    /// рідкісний <c>Row</c>/<c>Cell</c>-запис, беремо <c>Column</c>-область як
    /// пріоритетну, інакше — найменший <c>Id</c>. Це те саме спрощення «одна
    /// формула на колонку», яке вже мовчки робить <c>ExcelExporter</c>, записуючи
    /// формулу лише в перший рядок для не-<c>Row</c> області.
    /// </remarks>
    private static Dictionary<string, string> ColumnFormulas(
        TableDef table, Dictionary<int, ColumnDef> byColumn)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in table.Formulas
                     .Where(f => !f.IsDeleted && f.ColumnDefId is { } id && byColumn.ContainsKey(id))
                     .GroupBy(f => f.ColumnDefId!.Value))
        {
            var chosen = group
                .OrderBy(f => f.Scope == FormulaScope.Column ? 0 : 1)
                .ThenBy(f => f.Id)
                .First();
            result[byColumn[group.Key].Code] = chosen.Expression;
        }

        return result;
    }

    private static byte[] Zip(List<ExportTable> tables)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                var entry = zip.CreateEntry($"{i + 1:D3}-{t.Sheet}-{t.Table}.csv", CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                writer.Write(Csv(t));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Одна таблиця як CSV: заголовок — коди колонок, перша колонка — ключ рядка.</summary>
    /// <remarks>
    /// ⚠ Формули (коли просили) — ОКРЕМОЮ секцією після рядків даних, не
    /// додатковою колонкою: вираз — властивість колонки в цілому (метадані), а
    /// не значення рядка, і повторювати той самий текст у кожному рядку означало
    /// б і роздутий файл, і оманливий натяк, що вираз відрізняється по рядках.
    /// Той самий вибір форми, що й для <c>expression</c> у JSON нижче —
    /// властивість опису колонки, не значення рядка.
    /// </remarks>
    internal static string Csv(ExportTable table)
    {
        var text = new StringBuilder();
        text.Append(CsvFormat.Row([RowKeyHeader, .. table.Columns.Select(c => c.Code)]));
        foreach (var (key, values) in table.Rows)
        {
            text.Append(CsvFormat.Field(key));
            foreach (var column in table.Columns)
            {
                var value = values.GetValueOrDefault(column.Code);

                // ⚠ Число пишеться як є: захист від формул дописав би «'» до «-5»
                // і зіпсував би від'ємне число. Число в інваріантній культурі
                // формулою бути не може.
                text.Append(',').Append(IsNumeric(column) ? value : CsvFormat.Field(value));
            }

            text.Append(CsvFormat.NewLine);
        }

        if (table.ColumnFormulas.Count > 0)
        {
            text.Append(CsvFormat.NewLine);
            text.Append(CsvFormat.Row("formulas"));
            text.Append(CsvFormat.Row("column", "expression"));
            foreach (var column in table.Columns)
            {
                if (table.ColumnFormulas.TryGetValue(column.Code, out var expression))
                {
                    // ⚠ CsvFormat.Field і тут знешкоджує вираз від CSV-injection:
                    // вираз редактора може легально починатися з символів,
                    // з яких Excel будує іменем формулу (наприклад `-`, `+`).
                    text.Append(CsvFormat.Row(column.Code, expression));
                }
            }
        }

        return text.ToString();
    }

    private static byte[] Json(long documentId, int periodKey, int templateVersionId, List<ExportTable> tables)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("documentId", documentId);
            w.WriteNumber("periodKey", periodKey);
            w.WriteNumber("templateVersionId", templateVersionId);
            w.WriteStartArray("tables");
            foreach (var t in tables)
            {
                w.WriteStartObject();
                w.WriteString("sheet", t.Sheet);
                w.WriteString("table", t.Table);
                w.WriteStartArray("columns");
                foreach (var c in t.Columns)
                {
                    w.WriteStartObject();
                    w.WriteString("code", c.Code);
                    w.WriteString("dataType", c.DataType.ToString());
                    WriteNullable(w, "scale", c.Scale);
                    WriteNullable(w, "unitId", c.UnitId);

                    // ⚠ Поле лише для формульних колонок: коли просили формули,
                    // але в колонки їх нема, чи не просили зовсім — ключа
                    // "expression" в об'єкті немає взагалі (не null), щоб
                    // includeFormulas=false лишався побайтно тим самим виводом,
                    // що й до ФВ-4.2.
                    if (t.ColumnFormulas.TryGetValue(c.Code, out var expression))
                    {
                        w.WriteString("expression", expression);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteStartArray("rows");
                foreach (var (key, values) in t.Rows)
                {
                    w.WriteStartObject();
                    w.WriteString("rowKey", key);
                    w.WriteStartObject("values");

                    // ⚠ Усі значення — рядками, як decimal у всьому API (`D-30`).
                    foreach (var c in t.Columns)
                    {
                        if (values.TryGetValue(c.Code, out var v))
                        {
                            w.WriteString(c.Code, v);
                        }
                    }

                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<long, string>>> LookupsAsync(
        TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyDictionary<long, string>>();
        foreach (var registryId in snapshot.ColumnsById.Values
                     .Where(c => !c.IsDeleted && c.LookupRegistryDefId is not null)
                     .Select(c => c.LookupRegistryDefId!.Value)
                     .Distinct())
        {
            var entries = await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false);
            result[registryId] = entries.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().Code);
        }

        return result;
    }
}

/// <summary>Одна таблиця вивантаження.</summary>
/// <param name="Sheet">Код аркуша.</param>
/// <param name="Table">Код таблиці.</param>
/// <param name="Columns">Видимі колонки за порядком.</param>
/// <param name="Rows">Рядки: ключ і значення за кодом колонки.</param>
/// <param name="ColumnFormulas">
/// Код колонки → сирий вираз (ФВ-4.2); порожньо, коли формул не просили чи їх
/// нема.
/// </param>
internal sealed record ExportTable(
    string Sheet, string Table, IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<(string Key, Dictionary<string, string> Values)> Rows,
    IReadOnlyDictionary<string, string> ColumnFormulas);
