using System.Globalization;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Версія документа — зріз подання аркуша (ФВ-5.22).</summary>
public sealed record DocumentVersionDto(
    long VersionId, int SheetDefId, int PeriodKey, DateTime SubmittedAt, string SubmittedBy);

/// <summary>Змінена комірка; значення — рядком (decimal без втрати знаків).</summary>
public sealed record CellChangeDto(string TableCode, string RowKey, string ColumnCode, string? OldValue, string? NewValue);

/// <summary>Доданий або видалений рядок.</summary>
public sealed record RowChangeDto(long RowId, string TableCode, string RowKey);

/// <summary>
/// Різниця двох версій документа. <c>ToVersionId = null</c> — порівняння з поточним станом;
/// <c>Truncated</c> — хоч один перелік обрізано стелею <see cref="CompareDocumentVersionsHandler.MaxItems"/>.
/// </summary>
public sealed record DocumentCompareDto(
    long DocumentId,
    int PeriodKey,
    long FromVersionId,
    long? ToVersionId,
    IReadOnlyList<CellChangeDto> Changes,
    IReadOnlyList<RowChangeDto> AddedRows,
    IReadOnlyList<RowChangeDto> RemovedRows,
    bool Truncated);

/// <summary>Перелік версій документа за період. Право <c>Document.View</c>.</summary>
/// <remarks>Доступ вирішує <see cref="GetDocumentHandler"/>: чужий документ — той самий 404, що й неіснуючий.</remarks>
public sealed class ListDocumentVersionsHandler(GetDocumentHandler getDocument, IDocumentVersionStore versions)
{
    /// <summary>Право — те саме, що й перегляд документа.</summary>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Стеля переліку.</summary>
    public const int MaxVersions = 200;

    /// <summary>Зрізи подання, найновіші перші.</summary>
    public async Task<IReadOnlyList<DocumentVersionDto>> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var key = PeriodKey.Parse(periodKey);
        await DocumentVersionAccess.RequireAsync(getDocument, documentId, ct).ConfigureAwait(false);

        var list = await versions.ListAsync(documentId, key, MaxVersions, ct).ConfigureAwait(false);
        return list
            .Select(v => new DocumentVersionDto(
                v.Id, v.SheetDefId, v.PeriodKey, v.SubmittedAt,
                v.SubmittedByDisplayName ?? "#" + v.SubmittedByUserId.ToString(CultureInfo.InvariantCulture)))
            .ToList();
    }
}

/// <summary>Порівняння двох версій документа або версії з поточним станом. Право <c>Document.View</c>.</summary>
public sealed class CompareDocumentVersionsHandler(GetDocumentHandler getDocument, IDocumentVersionStore versions)
{
    /// <summary>Право — те саме, що й перегляд документа.</summary>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Стеля кожного з трьох переліків відповіді.</summary>
    public const int MaxItems = 1000;

    /// <summary>Значення параметра <c>to</c> для поточного стану.</summary>
    public const string Current = "current";

    /// <summary>Порівнює <paramref name="from"/> із <paramref name="to"/> (<c>current</c> або ідентифікатор версії).</summary>
    public async Task<DocumentCompareDto> HandleAsync(long documentId, long from, string? to, CancellationToken ct)
    {
        await DocumentVersionAccess.RequireAsync(getDocument, documentId, ct).ConfigureAwait(false);

        long? toId = null;
        if (!string.IsNullOrEmpty(to) && !string.Equals(to, Current, StringComparison.OrdinalIgnoreCase))
        {
            toId = long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw Invalid("err.ECR-DOC-0422.compareVersion", $"Версія «{to}» — не число і не «current».");
        }

        var older = await LoadAsync(documentId, from, ct).ConfigureAwait(false);
        var key = new PeriodKey(older.PeriodKey);

        IReadOnlyList<VersionCell> newerCells;
        if (toId is { } id)
        {
            var newer = await LoadAsync(documentId, id, ct).ConfigureAwait(false);
            if (newer.PeriodKey != older.PeriodKey)
            {
                // Рядки належать періоду: версії різних періодів не мають спільних рядків.
                throw Invalid("err.ECR-DOC-0422.comparePeriods", "Версії належать різним періодам.");
            }

            newerCells = Parse(newer.PayloadJson);
        }
        else
        {
            newerCells = await versions.ReadCurrentAsync(documentId, key, ct).ConfigureAwait(false);
        }

        return await DiffAsync(documentId, key, from, toId, Parse(older.PayloadJson), newerCells, ct).ConfigureAwait(false);
    }

    private async Task<DocumentCompareDto> DiffAsync(
        long documentId, PeriodKey key, long from, long? to,
        IReadOnlyList<VersionCell> oldCells, IReadOnlyList<VersionCell> newCells, CancellationToken ct)
    {
        var oldMap = oldCells.ToDictionary(c => (c.RowId, c.ColumnDefId), c => c.Value);
        var newMap = newCells.ToDictionary(c => (c.RowId, c.ColumnDefId), c => c.Value);
        var oldRows = oldCells.Select(c => c.RowId).ToHashSet();
        var newRows = newCells.Select(c => c.RowId).ToHashSet();

        var added = newRows.Except(oldRows).Order().ToList();
        var removed = oldRows.Except(newRows).Order().ToList();

        var changed = oldMap.Keys.Union(newMap.Keys)
            .Where(k => oldRows.Contains(k.RowId) && newRows.Contains(k.RowId))
            .Where(k => !SameValue(oldMap.GetValueOrDefault(k), newMap.GetValueOrDefault(k)))
            .OrderBy(k => k.RowId).ThenBy(k => k.ColumnDefId)
            .ToList();

        var truncated = changed.Count > MaxItems || added.Count > MaxItems || removed.Count > MaxItems;
        changed = changed.Take(MaxItems).ToList();
        added = added.Take(MaxItems).ToList();
        removed = removed.Take(MaxItems).ToList();

        var rows = await versions
            .DescribeRowsAsync(key, changed.Select(k => k.RowId).Concat(added).Concat(removed).ToHashSet(), ct)
            .ConfigureAwait(false);
        var columns = await versions
            .ColumnCodesAsync(changed.Select(k => k.ColumnDefId).ToHashSet(), ct)
            .ConfigureAwait(false);

        RowLabel Label(long rowId) => rows.GetValueOrDefault(rowId)
            ?? new RowLabel("", "#" + rowId.ToString(CultureInfo.InvariantCulture));

        return new DocumentCompareDto(
            documentId,
            key.Value,
            from,
            to,
            changed.Select(k => new CellChangeDto(
                Label(k.RowId).TableCode,
                Label(k.RowId).RowKey,
                columns.GetValueOrDefault(k.ColumnDefId) ?? "#" + k.ColumnDefId.ToString(CultureInfo.InvariantCulture),
                oldMap.GetValueOrDefault(k),
                newMap.GetValueOrDefault(k))).ToList(),
            added.Select(r => new RowChangeDto(r, Label(r).TableCode, Label(r).RowKey)).ToList(),
            removed.Select(r => new RowChangeDto(r, Label(r).TableCode, Label(r).RowKey)).ToList(),
            truncated);
    }

    /// <summary>
    /// Числа — як decimal (<c>1.50</c> = <c>1.5</c>, шістнадцятий знак розрізняється);
    /// відсутня комірка й порожня — одне й те саме; решта — порядкове порівняння тексту.
    /// </summary>
    internal static bool SameValue(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b);
        }

        return decimal.TryParse(a, NumberStyles.Number, CultureInfo.InvariantCulture, out var x)
               && decimal.TryParse(b, NumberStyles.Number, CultureInfo.InvariantCulture, out var y)
            ? x == y
            : string.Equals(a, b, StringComparison.Ordinal);
    }

    private async Task<DocumentVersionPayload> LoadAsync(long documentId, long versionId, CancellationToken ct)
        => await versions.FindAsync(documentId, versionId, ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               "ECR-DOC-0404",
               $"Версії {versionId} документа {documentId} немає.",
               new Dictionary<string, object?>(StringComparer.Ordinal)
               {
                   ["messageKey"] = "err.ECR-DOC-0404.version",
                   ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
                   ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
               });

    private static List<VersionCell> Parse(string payloadJson)
    {
        using var json = JsonDocument.Parse(payloadJson);
        return json.RootElement.EnumerateArray()
            .Select(e => new VersionCell(
                e.GetProperty("row").GetInt64(),
                e.GetProperty("column").GetInt32(),
                e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null))
            .ToList();
    }

    private static BusinessRuleException Invalid(string messageKey, string message)
        => new("ECR-DOC-0422", message, new Dictionary<string, object?>(StringComparer.Ordinal) { ["messageKey"] = messageKey });
}

/// <summary>Спільна перевірка доступу обох обробників версій.</summary>
internal static class DocumentVersionAccess
{
    /// <summary>Чужий і неіснуючий документ — однаковий 404, як у <c>GET /documents/{id}</c>.</summary>
    public static async Task RequireAsync(GetDocumentHandler getDocument, long documentId, CancellationToken ct)
    {
        if (await getDocument.HandleAsync(documentId, null, ct).ConfigureAwait(false) is null)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }
}
