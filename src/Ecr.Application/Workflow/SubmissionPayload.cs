using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Формат <c>calc.SubmissionSnapshot.PayloadJson</c> (ФВ-5.7): запис і читання в одному місці.
/// </summary>
/// <remarks>
/// ⛔ Доти писалося лише <c>ValueNumeric ?? ValueString</c>: дата, булеве, запис довідника й
/// одиниця потрапляли в зріз як <c>null</c> — нерозрізненно з порожньою клітинкою.
/// ⚠ Сумісність: число, текст і порожнеча пишуться байт-у-байт як раніше (без ключа
/// <c>type</c>), тож старі зрізи читаються як були, а їхній <c>ContentHash</c> не змінюється.
/// Інші типи — той самий <c>value</c> рядком плюс ключ <c>type</c>.
/// </remarks>
public static class SubmissionPayload
{
    public const string Date = "date";
    public const string Bool = "bool";
    public const string RegistryEntry = "ref";
    public const string Unit = "unit";

    /// <summary>Серіалізує клітинки у стабільному порядку (рядок, колонка).</summary>
    public static string Write(IEnumerable<CellRecord> cells)
        => JsonSerializer.Serialize(cells
            .OrderBy(c => c.Address.TableRowId)
            .ThenBy(c => c.Address.ColumnDefId)
            .Select(c => Encode(c.Address.TableRowId, c.Address.ColumnDefId, c.Value)));

    /// <summary>Читає payload будь-якого покоління (з <c>type</c> і без).</summary>
    public static IReadOnlyList<SubmissionPayloadCell> Read(string payloadJson)
        => JsonSerializer.Deserialize<List<SubmissionPayloadCell>>(payloadJson) ?? [];

    private static SubmissionPayloadCell Encode(long row, int column, CellValueData v)
    {
        if (v.ValueNumeric is { } n)
        {
            return new(row, column, n.ToString(CultureInfo.InvariantCulture), null);
        }
        if (v.ValueString is { } s)
        {
            return new(row, column, s, null);
        }
        if (v.ValueDate is { } d)
        {
            return new(row, column, d.ToString("O", CultureInfo.InvariantCulture), Date);
        }
        if (v.ValueBool is { } b)
        {
            return new(row, column, b ? "true" : "false", Bool);
        }
        if (v.ValueRegistryEntryId is { } r)
        {
            return new(row, column, r.ToString(CultureInfo.InvariantCulture), RegistryEntry);
        }
        if (v.ValueUnitId is { } u)
        {
            return new(row, column, u.ToString(CultureInfo.InvariantCulture), Unit);
        }
        return new(row, column, null, null);
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
