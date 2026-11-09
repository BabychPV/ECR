using System.Data;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;

namespace Ecr.Infrastructure.Persistence;

/// <summary>(j6) Підписи записів журналу структури про історію дозволу (<c>PermitSegment</c>) і поділ місяця (<c>CellPermitPart</c>).</summary>
public sealed partial class AuditReader
{
    /// <summary>Типи записів, чий підпис будується з JSON самого запису, а не з чинної таблиці.</summary>
    private static readonly HashSet<string> PermitHistoryEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PermitSegment",
        "CellPermitPart",
    };

    /// <summary>
    /// «номер документа · таблиця/рядок · дозволи · дата/місяць» замість сирого JSON. Сегмент після «Undo last» видалено з
    /// <c>doc.PermitSegment</c>, а поділ має ключем колонку місяця (кілька записів на один <c>EntityId</c>) — тому підпис
    /// будується з <c>NewJson</c>/<c>OldJson</c> КОЖНОГО запису; з бази — лише номер документа (один запит по сторінці).
    /// </summary>
    /// <remarks>Нечитабельний або старіший JSON — без підпису (клієнт покаже ідентифікатор), читання не падає.</remarks>
    private static async Task<IReadOnlyList<StructureChangeView>> WithPermitHistoryLabelsAsync(
        SqlConnection connection, List<StructureChangeView> items, CancellationToken ct)
    {
        var parsed = new Dictionary<int, PermitAuditState>();
        for (var i = 0; i < items.Count; i++)
        {
            if (PermitHistoryEntityTypes.Contains(TrimSchema(items[i].EntityType))
                && PermitAuditState.TryParse(items[i].NewJson ?? items[i].OldJson) is { } state)
            {
                parsed[i] = state;
            }
        }

        if (parsed.Count == 0)
        {
            return items;
        }

        var documents = new Dictionary<long, string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT Id, BusinessKey FROM doc.Document WHERE Id IN (SELECT CAST([value] AS bigint) FROM OPENJSON(@ids));";
            command.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value =
                "[" + string.Join(",", parsed.Values.Select(s => s.DocumentId.ToString(CultureInfo.InvariantCulture)).Distinct()) + "]";
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                documents[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        // Row names (h+ rule: a row is named as people see it — «Sea Water Intake», not «R1»): the template row's label
        // (en, the stored default language) of the document's rows with these table codes and keys; a row without a template
        // label (dynamic row) keeps its key. ⛔ RS: the segment/part carries its sheet copy — the row is taken from THAT copy's
        // instance (SheetCopyId NULL = the original), never from a sibling copy with the same row key.
        var rowNames = new Dictionary<(long DocumentId, string Table, long CopyId, string RowKey), (string? Label, int Position)>();
        var keyed = parsed.Values.Where(s => s.Table is not null && s.RowKey is not null).ToList();
        if (keyed.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT ti.DocumentId, td.Code, r.RowKey, JSON_VALUE(rd.LabelL10n, N'$.en'), COALESCE(ti.SheetCopyId, 0),
                       (SELECT COUNT(*) FROM doc.TableRow AS o
                         WHERE o.PeriodKey = r.PeriodKey AND o.TableInstanceId = r.TableInstanceId AND o.IsDeleted = 0
                           AND o.Ordinal <= r.Ordinal)
                FROM doc.TableRow AS r
                JOIN doc.TableInstance AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
                JOIN cfg.TableDef AS td ON td.Id = ti.TableDefId
                LEFT JOIN cfg.RowDef AS rd ON rd.Id = r.RowDefId OR (r.RowDefId IS NULL AND rd.TableDefId = ti.TableDefId AND rd.RowKey = r.RowKey)
                WHERE ti.DocumentId IN (SELECT CAST([value] AS bigint) FROM OPENJSON(@docs))
                  AND r.RowKey IN (SELECT [value] FROM OPENJSON(@keys));
                """;
            command.Parameters.Add("@docs", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(keyed.Select(s => s.DocumentId).Distinct());
            command.Parameters.Add("@keys", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(keyed.Select(s => s.RowKey).Distinct());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rowNames[(reader.GetInt64(0), reader.GetString(1), Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture), reader.GetString(2))] =
                    (reader.IsDBNull(3) ? null : reader.GetString(3), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture));
            }
        }

        return [.. items.Select((item, i) => parsed.TryGetValue(i, out var s)
            ? item with
            {
                EntityLabel = s.Label(
                    documents.GetValueOrDefault(s.DocumentId),
                    s.Table is null || s.RowKey is null
                        ? null
                        : rowNames.TryGetValue((s.DocumentId, s.Table, s.SheetCopyId ?? 0, s.RowKey), out var found)
                            ? Ecr.Application.Permits.History.PermitTimeline.RowName(found.Label, found.Position, s.BasePermit)
                            : Ecr.Application.Permits.History.PermitTimeline.RowName(null, 0, s.BasePermit)),
            }
            : item)];
    }

    /// <summary>Поля JSON запису, потрібні підпису.</summary>
    private sealed record PermitAuditState(
        long DocumentId, string? Table, string? RowKey, string? Column, string? HeaderField, string? ValidFrom, string? Permit,
        string? PreviousPermit, string? Month, IReadOnlyList<string> PartPermits, long? SheetCopyId,
        IReadOnlyList<string?> PartValues, string? Total)
    {
        /// <summary>The row's (base) permit for a row without a name: the previous permit of a change, else part A's.</summary>
        public string? BasePermit => PreviousPermit ?? (PartPermits.Count > 0 ? PartPermits[0] : Permit);

        public static PermitAuditState? TryParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("documentId", out var id) || !id.TryGetInt64(out var documentId))
                {
                    return null;
                }

                var partItems = root.TryGetProperty("parts", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Where(x => Text(x, "permit") is not null).ToList()
                    : [];
                var parts = partItems.Select(x => Text(x, "permit")!).ToList();
                return new PermitAuditState(
                    documentId, Text(root, "table"), Text(root, "rowKey"), Text(root, "column"), Text(root, "headerField"),
                    Text(root, "validFrom"), Text(root, "permit"), Text(root, "previousPermit"), Text(root, "month"), parts,
                    root.TryGetProperty("sheetCopyId", out var copy) && copy.ValueKind == JsonValueKind.Number && copy.TryGetInt64(out var copyId)
                        ? copyId
                        : null,
                    [.. partItems.Select(x => Number(x, "value"))],
                    Number(root, "total"));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Зміна дозволу: <c>DOC-1 · Sea Water Intake · A → B · 2026-03-15</c> (рядок без назви — <c>T/R1</c>; поле шапки —
        /// <c>DOC-1 · PERMIT_H · …</c>); поділ: <c>DOC-1 · Sea Water Intake · M03 · 2026-03 · A + B</c>.
        /// </summary>
        public string Label(string? documentKey, string? rowName)
        {
            var parts = new List<string> { documentKey ?? $"#{DocumentId.ToString(CultureInfo.InvariantCulture)}" };
            Add(parts, Table is null ? HeaderField : rowName ?? (RowKey is null ? Table : $"{Table}/{RowKey}"));
            if (Month is not null)
            {
                Add(parts, Column);
                Add(parts, Month);
                // F13 p220 j6: the part values A and B and the total, not only the permits.
                Add(parts, PartPermits.Count == 0
                    ? null
                    : string.Join(" + ", PartPermits.Select((permit, i) =>
                          PartValues.ElementAtOrDefault(i) is { } value ? $"{permit}: {value}" : permit))
                      + (Total is null ? string.Empty : $" = {Total}"));
            }
            else
            {
                Add(parts, PreviousPermit is null ? Permit : $"{PreviousPermit} → {Permit ?? "…"}");
                Add(parts, ValidFrom);
            }

            return string.Join(" · ", parts);
        }

        private static void Add(List<string> parts, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(value);
            }
        }

        /// <summary>A JSON number as people read it: «5.5», «12» (no trailing zeros); anything else — <c>null</c>.</summary>
        private static string? Number(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
                ? (number / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture)
                : null;

        private static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
