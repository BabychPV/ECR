using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Формат <c>calc.SubmissionSnapshot.PayloadJson</c> (ФВ-5.7, ФВ-9.4): запис і читання в
/// одному місці.
/// </summary>
/// <remarks>
/// ⛔ Доти писалося лише <c>ValueNumeric ?? ValueString</c>: дата, булеве, запис довідника й
/// одиниця потрапляли в зріз як <c>null</c> — нерозрізненно з порожньою клітинкою.
/// ⚠ Сумісність (клітинки): число, текст і порожнеча пишуться байт-у-байт як раніше (без
/// ключа <c>type</c>), тож старі зрізи читаються як були, а їхній <c>ContentHash</c> не
/// змінюється. Інші типи — той самий <c>value</c> рядком плюс ключ <c>type</c>.
/// ⚠ Сумісність (шапка, ФВ-9.4): доки в поданні немає жодного значення шапки, <see cref="Write"/>
/// повертає ГОЛИЙ масив клітинок — рівно як до додавання секції <c>header</c>, байт-у-байт.
/// Секція <c>header</c> з'являється лише тоді, коли є що в неї писати: тоді payload — об'єкт
/// <c>{"cells": [...], "header": {...}}</c>. <see cref="Read"/> і <see cref="ReadHeader"/>
/// приймають ОБИДВІ форми; зрізи, збережені до цієї зміни (голий масив, секції <c>header</c>
/// немає взагалі), не падають — <see cref="ReadHeader"/> віддає для них порожній словник.
/// </remarks>
public static class SubmissionPayload
{
    public const string Date = "date";
    public const string Bool = "bool";
    public const string RegistryEntry = "ref";
    public const string Unit = "unit";

    private static readonly IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> EmptyHeader =
        new Dictionary<string, SubmissionPayloadHeaderValue>(StringComparer.Ordinal);

    /// <summary>
    /// Серіалізує клітинки (стабільний порядок: рядок, колонка) і, якщо є, знімок шапки
    /// документа на момент подання (стабільний порядок: код поля).
    /// </summary>
    /// <param name="cells">Клітинки поданого аркуша.</param>
    /// <param name="header">
    /// Знімок значень шапки документа, ключований кодом поля; <c>null</c> або порожній
    /// словник — секція <c>header</c> у payload не з'являється взагалі (сумісність, див.
    /// <see cref="SubmissionPayload"/>).
    /// </param>
    public static string Write(
        IEnumerable<CellRecord> cells,
        IReadOnlyDictionary<string, DocumentHeaderValueData>? header = null)
    {
        var cellsPayload = cells
            .OrderBy(c => c.Address.TableRowId)
            .ThenBy(c => c.Address.ColumnDefId)
            .Select(c => Encode(c.Address.TableRowId, c.Address.ColumnDefId, c.Value))
            .ToList();

        if (header is not { Count: > 0 })
        {
            return JsonSerializer.Serialize(cellsPayload);
        }

        var headerPayload = new SortedDictionary<string, SubmissionPayloadHeaderValue>(StringComparer.Ordinal);
        foreach (var (code, value) in header)
        {
            headerPayload[code] = EncodeHeader(value);
        }

        return JsonSerializer.Serialize(new SubmissionPayloadEnvelope(cellsPayload, headerPayload));
    }

    /// <summary>Читає клітинки payload будь-якого покоління (голий масив, з <c>type</c> і без,
    /// або об'єкт із секцією <c>cells</c>).</summary>
    public static IReadOnlyList<SubmissionPayloadCell> Read(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        // ⚠ Зрізи до ФВ-9.4 — голий масив клітинок; нові без секції header лишаються
        // голим масивом теж (див. Write).
        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<SubmissionPayloadCell>>(root.GetRawText()) ?? [];
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("cells", out var cellsElement))
        {
            return JsonSerializer.Deserialize<List<SubmissionPayloadCell>>(cellsElement.GetRawText()) ?? [];
        }

        return [];
    }

    /// <summary>
    /// Читає знімок шапки документа на момент подання. Зріз без секції <c>header</c> —
    /// голий масив клітинок (старе покоління) або об'єкт без цього поля (нове покоління,
    /// шапка була порожня на момент подання) — дає порожній словник, не падає.
    /// </summary>
    public static IReadOnlyDictionary<string, SubmissionPayloadHeaderValue> ReadHeader(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("header", out var headerElement)
            || headerElement.ValueKind != JsonValueKind.Object)
        {
            return EmptyHeader;
        }

        return JsonSerializer.Deserialize<Dictionary<string, SubmissionPayloadHeaderValue>>(headerElement.GetRawText())
               ?? EmptyHeader;
    }

    private static SubmissionPayloadCell Encode(long row, int column, CellValueData v)
    {
        var (value, type) = EncodeTyped(
            v.ValueNumeric, v.ValueString, v.ValueDate, v.ValueBool, v.ValueRegistryEntryId, v.ValueUnitId);
        return new(row, column, value, type);
    }

    private static SubmissionPayloadHeaderValue EncodeHeader(DocumentHeaderValueData v)
    {
        var (value, type) = EncodeTyped(
            v.ValueNumeric, v.ValueString, v.ValueDate, v.ValueBool, v.ValueRegistryEntryId, v.ValueUnitId);
        return new(value, type);
    }

    /// <summary>Той самий розбір типів для клітинки й поля шапки (той самий набір Value*, R-B4).</summary>
    private static (string? Value, string? Type) EncodeTyped(
        decimal? valueNumeric, string? valueString, DateTime? valueDate, bool? valueBool,
        long? valueRegistryEntryId, int? valueUnitId)
    {
        if (valueNumeric is { } n)
        {
            return (n.ToString(CultureInfo.InvariantCulture), null);
        }
        if (valueString is { } s)
        {
            return (s, null);
        }
        if (valueDate is { } d)
        {
            return (d.ToString("O", CultureInfo.InvariantCulture), Date);
        }
        if (valueBool is { } b)
        {
            return (b ? "true" : "false", Bool);
        }
        if (valueRegistryEntryId is { } r)
        {
            return (r.ToString(CultureInfo.InvariantCulture), RegistryEntry);
        }
        if (valueUnitId is { } u)
        {
            return (u.ToString(CultureInfo.InvariantCulture), Unit);
        }
        return (null, null);
    }
}

/// <summary>Одна клітинка зрізу подання.</summary>
/// <param name="Row">Ідентифікатор рядка таблиці.</param>
/// <param name="Column">Ідентифікатор колонки.</param>
/// <param name="Value">Значення рядком; <c>null</c> — порожньо.</param>
/// <param name="Type">
/// <c>null</c> — число або текст (формат до виправлення, їх він не розрізняв);
/// інакше одна з констант <see cref="SubmissionPayload"/>.
/// </param>
public sealed record SubmissionPayloadCell(
    [property: JsonPropertyName("row")] long Row,
    [property: JsonPropertyName("column")] int Column,
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type);

/// <summary>
/// Одне значення шапки документа в зрізі подання (ФВ-9.4) — знімок на момент подання, не
/// посилання: якщо шапку пізніше змінять, уже поданий зріз зберігає те, що було правдою тоді.
/// </summary>
/// <param name="Value">Значення рядком; <c>null</c> — явна порожнеча (R-B4).</param>
/// <param name="Type">
/// <c>null</c> — число або текст; інакше одна з констант <see cref="SubmissionPayload"/>
/// (<c>date</c>/<c>bool</c>/<c>ref</c>/<c>unit</c>) — той самий формат, що й
/// <see cref="SubmissionPayloadCell"/>.
/// </param>
public sealed record SubmissionPayloadHeaderValue(
    [property: JsonPropertyName("value")] string? Value,
    [property: JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Type);

/// <summary>Форма payload, коли є секція <c>header</c> — інакше голий масив клітинок (сумісність).</summary>
internal sealed record SubmissionPayloadEnvelope(
    [property: JsonPropertyName("cells")] List<SubmissionPayloadCell> Cells,
    [property: JsonPropertyName("header")] SortedDictionary<string, SubmissionPayloadHeaderValue> Header);
