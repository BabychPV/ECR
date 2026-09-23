using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Версія документа — зріз подання аркуша (ФВ-5.22).</summary>
public sealed record DocumentVersionDto(
    long VersionId, int SheetDefId, int PeriodKey, DateTime SubmittedAt, string SubmittedBy);

/// <summary>
/// Змінена комірка; значення — рядком (decimal без втрати знаків). Тип — як у зрізі подання
/// (<see cref="SubmissionPayloadCell.Type"/>): <c>null</c> — число або текст, інакше
/// <c>date|bool|ref|unit</c>.
/// </summary>
public sealed record CellChangeDto(
    string TableCode, string RowKey, string ColumnCode,
    string? OldValue, string? NewValue, string? OldType, string? NewType);

/// <summary>Доданий або видалений рядок.</summary>
public sealed record RowChangeDto(long RowId, string TableCode, string RowKey);

/// <summary>
/// Змінене поле шапки документа (ФВ-9.4); значення — рядком, тип — та сама конвенція, що й
/// <see cref="CellChangeDto"/> (<c>null</c> — число або текст, інакше <c>date|bool|ref|unit</c>).
/// Без людської назви поля (Label): клієнт резолвить її сам через метадані версії шаблону
/// (<c>HeaderFieldDef</c>) — та сама симетрія, що вже прийнята для Lookup-полів шапки
/// (<c>DocumentHeaderFieldDto</c> теж не несе назви обраного запису).
/// </summary>
public sealed record HeaderFieldChangeDto(
    string Code, string? OldValue, string? NewValue, string? OldType, string? NewType);

/// <summary>
/// Різниця двох версій документа. <c>ToVersionId = null</c> — порівняння з поточним станом;
/// <c>Truncated</c> — хоч один перелік обрізано стелею <see cref="CompareDocumentVersionsHandler.MaxItems"/>
/// (лише клітинки й рядки — полів шапки завжди мало, окрема стеля для них не потрібна).
/// </summary>
public sealed record DocumentCompareDto(
    long DocumentId,
    int PeriodKey,
    long FromVersionId,
    long? ToVersionId,
    IReadOnlyList<CellChangeDto> Changes,
    IReadOnlyList<RowChangeDto> AddedRows,
    IReadOnlyList<RowChangeDto> RemovedRows,
    bool Truncated,
    IReadOnlyList<HeaderFieldChangeDto> HeaderChanges);

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
public sealed class CompareDocumentVersionsHandler(
    GetDocumentHandler getDocument,
    IDocumentVersionStore versions,
    IDocumentStore documents,
    IMetadataCache metadata,
    IDocumentHeaderStore headers)
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

        IReadOnlyList<SubmissionPayloadCell> newerCells;
        IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> newerHeader;
        if (toId is { } id)
        {
            var newer = await LoadAsync(documentId, id, ct).ConfigureAwait(false);
            if (newer.PeriodKey != older.PeriodKey)
            {
                // Рядки належать періоду: версії різних періодів не мають спільних рядків.
                throw Invalid("err.ECR-DOC-0422.comparePeriods", "Версії належать різним періодам.");
            }

            newerCells = SubmissionPayload.Read(newer.PayloadJson);
            newerHeader = SubmissionPayload.ReadHeader(newer.PayloadJson);
        }
        else
        {
            newerCells = await versions.ReadCurrentAsync(documentId, key, ct).ConfigureAwait(false);
            newerHeader = await ReadCurrentHeaderAsync(documentId, ct).ConfigureAwait(false);
        }

        return await DiffAsync(
            documentId, key, from, toId,
            SubmissionPayload.Read(older.PayloadJson), newerCells,
            SubmissionPayload.ReadHeader(older.PayloadJson), newerHeader,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Живі значення шапки документа, закодовані в ту саму форму (Value, Type), що зберігає
    /// зріз подання (<see cref="SubmissionPayload.EncodeHeader"/>) — щоб порівнювати з
    /// <c>ReadHeader</c> без другого розбору типів. Поле без запису в <c>headers</c> — те саме
    /// «немає в стані», що й відсутній ключ у зрізі (<see cref="SubmissionPayload.ReadHeader"/>).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, SubmissionPayloadHeaderValue>> ReadCurrentHeaderAsync(
        long documentId, CancellationToken ct)
    {
        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var values = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);

        var result = new Dictionary<string, SubmissionPayloadHeaderValue>(StringComparer.Ordinal);
        foreach (var field in snapshot.HeaderFields)
        {
            if (values.TryGetValue(field.Id, out var value))
            {
                result[field.Code] = SubmissionPayload.EncodeHeader(value);
            }
        }

        return result;
    }

    private async Task<DocumentCompareDto> DiffAsync(
        long documentId, PeriodKey key, long from, long? to,
        IReadOnlyList<SubmissionPayloadCell> oldCells, IReadOnlyList<SubmissionPayloadCell> newCells,
        IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> oldHeader,
        IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> newHeader,
        CancellationToken ct)
    {
        var oldMap = oldCells.ToDictionary(c => (RowId: c.Row, ColumnDefId: c.Column));
        var newMap = newCells.ToDictionary(c => (RowId: c.Row, ColumnDefId: c.Column));
        var oldRows = oldCells.Select(c => c.Row).ToHashSet();
        var newRows = newCells.Select(c => c.Row).ToHashSet();

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

        // Поле присутнє лише в одному стані — теж зміна (проти порожнього, той самий підхід,
        // що для клітинок вище): GetValueOrDefault для відсутнього коду дає null, а SameValue(null, x)
        // порівнює це як порожнє значення.
        var headerChanges = oldHeader.Keys
            .Union(newHeader.Keys, StringComparer.Ordinal)
            .Where(code => !SameValue(oldHeader.GetValueOrDefault(code), newHeader.GetValueOrDefault(code)))
            .OrderBy(code => code, StringComparer.Ordinal)
            .Select(code => new HeaderFieldChangeDto(
                code,
                oldHeader.GetValueOrDefault(code)?.Value,
                newHeader.GetValueOrDefault(code)?.Value,
                oldHeader.GetValueOrDefault(code)?.Type,
                newHeader.GetValueOrDefault(code)?.Type))
            .ToList();

        return new DocumentCompareDto(
            documentId,
            key.Value,
            from,
            to,
            changed.Select(k => new CellChangeDto(
                Label(k.RowId).TableCode,
                Label(k.RowId).RowKey,
                columns.GetValueOrDefault(k.ColumnDefId) ?? "#" + k.ColumnDefId.ToString(CultureInfo.InvariantCulture),
                oldMap.GetValueOrDefault(k)?.Value,
                newMap.GetValueOrDefault(k)?.Value,
                oldMap.GetValueOrDefault(k)?.Type,
                newMap.GetValueOrDefault(k)?.Type)).ToList(),
            added.Select(r => new RowChangeDto(r, Label(r).TableCode, Label(r).RowKey)).ToList(),
            removed.Select(r => new RowChangeDto(r, Label(r).TableCode, Label(r).RowKey)).ToList(),
            truncated,
            headerChanges);
    }

    /// <summary>
    /// Порівняння за парою (значення, тип): різний тип — зміна (текст <c>"true"</c> ≠ булеве <c>true</c>);
    /// відсутня комірка й порожня — одне й те саме незалежно від типу; дата — як дата;
    /// без типу числа — як decimal (<c>1.50</c> = <c>1.5</c>, шістнадцятий знак розрізняється),
    /// решта — порядкове порівняння тексту.
    /// </summary>
    /// <remarks>
    /// ⚠ Зріз до виправлення формату писав дату, булеве, довідник і одиницю як <c>null</c> без типу.
    /// Маркера покоління в зрізі немає, а порожня клітинка нового формату виглядає так само,
    /// тож такий зріз надійно не розпізнати: ці значення показуються як зміна від порожнього.
    /// </remarks>
    internal static bool SameValue(SubmissionPayloadCell? a, SubmissionPayloadCell? b)
        => SameTyped(a?.Value, a?.Type, b?.Value, b?.Type);

    /// <summary>
    /// Той самий порівняльний контракт, що для клітинок — поле шапки кодується в ту саму пару
    /// (Value, Type) (<see cref="SubmissionPayload.EncodeHeader"/>/<see cref="SubmissionPayload.ReadHeader"/>),
    /// тож друге визначення правил порівняння типів тут не потрібне.
    /// </summary>
    internal static bool SameValue(SubmissionPayloadHeaderValue? a, SubmissionPayloadHeaderValue? b)
        => SameTyped(a?.Value, a?.Type, b?.Value, b?.Type);

    private static bool SameTyped(string? av, string? at, string? bv, string? bt)
    {
        if (string.IsNullOrEmpty(av) || string.IsNullOrEmpty(bv))
        {
            return string.IsNullOrEmpty(av) && string.IsNullOrEmpty(bv);
        }

        if (!string.Equals(at, bt, StringComparison.Ordinal))
        {
            return false;
        }

        if (at == SubmissionPayload.Date)
        {
            return DateTime.TryParse(av, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var p)
                   && DateTime.TryParse(bv, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var q)
                ? p == q
                : string.Equals(av, bv, StringComparison.Ordinal);
        }

        if (at is not null)
        {
            return string.Equals(av, bv, StringComparison.Ordinal);
        }

        return SameUntyped(av, bv);
    }

    private static bool SameUntyped(string a, string b)
    {
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
