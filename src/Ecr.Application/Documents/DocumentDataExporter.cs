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
/// ⚠ Формат їде суфіксом у <c>exportId</c> (<c>…-csv</c>, <c>…-json</c>): сховище
/// експорту пам'ятає лише байти й документ, а завантаження мусить знати тип вмісту.
/// Для <c>xlsx</c> ключ лишається голим GUID — наявні посилання не змінюються.
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

    /// <summary>Ключ експорту з форматом.</summary>
    public static string NewExportId(string format)
        => format == Xlsx ? Guid.NewGuid().ToString("N") : $"{Guid.NewGuid():N}-{format}";

    /// <summary>Тип вмісту й розширення за ключем експорту.</summary>
    public static (string ContentType, string Extension) OfExportId(string exportId)
    {
        ArgumentNullException.ThrowIfNull(exportId);
        if (exportId.EndsWith("-" + Csv, StringComparison.Ordinal))
        {
            return ("application/zip", "zip");
        }

        return exportId.EndsWith("-" + Json, StringComparison.Ordinal)
            ? ("application/json", "json")
            : ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
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
    IRegistryStore registries)
{
    private const string RowKeyHeader = "rowKey";

    /// <summary>Будує файл вивантаження.</summary>
    public async Task<byte[]> ExportAsync(long documentId, int periodKey, string format, CancellationToken ct)
    {
        var key = new PeriodKey(periodKey);
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
                    slices.GetValueOrDefault(instanceId) ?? []));
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
        IReadOnlyList<CellRecord> cells)
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

        return new ExportTable(sheet.Code, table.Code, columns, keys.Select(k => (k, values[k])).ToList());
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
internal sealed record ExportTable(
    string Sheet, string Table, IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<(string Key, Dictionary<string, string> Values)> Rows);
