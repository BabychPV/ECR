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

        return [.. items.Select((item, i) => parsed.TryGetValue(i, out var s)
            ? item with { EntityLabel = s.Label(documents.GetValueOrDefault(s.DocumentId)) }
            : item)];
    }

    /// <summary>Поля JSON запису, потрібні підпису.</summary>
    private sealed record PermitAuditState(
        long DocumentId, string? Table, string? RowKey, string? Column, string? HeaderField, string? ValidFrom, string? Permit,
        string? PreviousPermit, string? Month, IReadOnlyList<string> PartPermits)
    {
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

                var parts = root.TryGetProperty("parts", out var p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(x => Text(x, "permit")).Where(x => x is not null).Select(x => x!).ToList()
                    : [];
                return new PermitAuditState(
                    documentId, Text(root, "table"), Text(root, "rowKey"), Text(root, "column"), Text(root, "headerField"),
                    Text(root, "validFrom"), Text(root, "permit"), Text(root, "previousPermit"), Text(root, "month"), parts);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Зміна дозволу: <c>DOC-1 · T/R1 · A → B · 2026-03-15</c> (поле шапки — <c>DOC-1 · PERMIT_H · …</c>);
        /// поділ: <c>DOC-1 · T/R1 · M03 · 2026-03 · A + B</c>.
        /// </summary>
        public string Label(string? documentKey)
        {
            var parts = new List<string> { documentKey ?? $"#{DocumentId.ToString(CultureInfo.InvariantCulture)}" };
            Add(parts, Table is null ? HeaderField : RowKey is null ? Table : $"{Table}/{RowKey}");
            if (Month is not null)
            {
                Add(parts, Column);
                Add(parts, Month);
                Add(parts, PartPermits.Count > 0 ? string.Join(" + ", PartPermits) : null);
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

        private static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
