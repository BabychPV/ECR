// src/Ecr.Application/Documents/HeaderValueReader.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Приводить значення поля шапки з запиту до <see cref="DocumentHeaderValueData"/>
/// за <b>оголошеним типом поля</b> — той самий контракт, що
/// <see cref="CellValueReader.Read"/>: тип диктує поле, а не вміст значення,
/// і розгортання <see cref="System.Text.Json.JsonElement"/> йде тим самим
/// шляхом (<see cref="CellValueReader.Normalize"/>).
/// </summary>
public static class HeaderValueReader
{
    /// <summary>
    /// Будує значення поля шапки за описом поля.
    /// </summary>
    /// <param name="raw">Значення з запиту.</param>
    /// <param name="field">Опис поля; його <c>DataType</c> і вирішує.</param>
    /// <returns>Значення для запису; <c>null</c> — поле треба стерти (R-B4).</returns>
    /// <exception cref="BusinessRuleException">
    /// Значення не відповідає типу поля — <c>ECR-HDR-0422</c>.
    /// </exception>
    public static DocumentHeaderValueData? Read(object? raw, HeaderFieldDef field)
    {
        ArgumentNullException.ThrowIfNull(field);

        var value = CellValueReader.Normalize(raw);

        if (value is null)
        {
            return null;
        }

        return field.DataType switch
        {
            CellDataType.Int or CellDataType.Decimal
                => new DocumentHeaderValueData { ValueNumeric = Number(value, field) },

            CellDataType.Bool => new DocumentHeaderValueData { ValueBool = Boolean(value, field) },
            CellDataType.Date => new DocumentHeaderValueData { ValueDate = Date(value, field) },
            CellDataType.Lookup => new DocumentHeaderValueData { ValueRegistryEntryId = Identifier(value, field) },
            CellDataType.Unit => new DocumentHeaderValueData { ValueUnitId = UnitIdentifier(value, field) },

            _ => new DocumentHeaderValueData { ValueString = Text(value) },
        };
    }

    private static decimal Number(object value, HeaderFieldDef field) => value switch
    {
        decimal number => number,
        int number => number,
        long number => number,
        short number => number,
        double number => (decimal)number,
        float number => (decimal)number,
        bool flag => flag ? 1m : 0m,
        string text when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(field, value, "число"),
    };

    private static bool Boolean(object value, HeaderFieldDef field) => value switch
    {
        bool flag => flag,
        decimal number => number != 0m,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(field, value, "булеве значення"),
    };

    private static DateTime Date(object value, HeaderFieldDef field) => value switch
    {
        DateTime date => date,
        DateTimeOffset offset => offset.UtcDateTime,
        string text when CellDateParser.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(field, value, "дата"),
    };

    private static long Identifier(object value, HeaderFieldDef field) => value switch
    {
        int identifier => identifier,
        long identifier => identifier,
        decimal identifier when decimal.Truncate(identifier) == identifier => (long)identifier,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(field, value, "ідентифікатор"),
    };

    /// <summary>Ідентифікатор одиниці — той самий діапазон, що <c>int</c>-колонка UOM.</summary>
    private static int UnitIdentifier(object value, HeaderFieldDef field) => value switch
    {
        int identifier => identifier,
        long identifier when identifier is >= int.MinValue and <= int.MaxValue => (int)identifier,
        decimal identifier when decimal.Truncate(identifier) == identifier
                                && identifier is >= int.MinValue and <= int.MaxValue
            => (int)identifier,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(field, value, "ідентифікатор"),
    };

    private static string Text(object value) => value switch
    {
        string text => text,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static BusinessRuleException Mismatch(HeaderFieldDef field, object value, string expected)
        => new(
            ErrorCodes.HeaderValueInvalid,
            $"Поле шапки «{field.Code}» очікує {expected}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-HDR-0422.typeMismatch",
                ["headerFieldCode"] = field.Code,
                ["expected"] = expected,
                ["actualKind"] = value.GetType().Name,
            });
}
