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
                => new DocumentHeaderValueData { ValueNumeric = Storable(Number(value, field), field) },

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
        _ => throw Mismatch(field, value, ExpectedType.Number),
    };

    private static bool Boolean(object value, HeaderFieldDef field) => value switch
    {
        bool flag => flag,
        decimal number => number != 0m,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(field, value, ExpectedType.Boolean),
    };

    private static DateTime Date(object value, HeaderFieldDef field) => value switch
    {
        DateTime date => date,
        DateTimeOffset offset => offset.UtcDateTime,
        string text when CellDateParser.TryParse(text, out var parsed) => parsed,
        _ => throw Mismatch(field, value, ExpectedType.Date),
    };

    private static long Identifier(object value, HeaderFieldDef field) => value switch
    {
        int identifier => identifier,
        long identifier => identifier,
        decimal identifier when decimal.Truncate(identifier) == identifier => (long)identifier,
        string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            => parsed,
        _ => throw Mismatch(field, value, ExpectedType.Identifier),
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
        _ => throw Mismatch(field, value, ExpectedType.Identifier),
    };

    private static string Text(object value) => value switch
    {
        string text => text,
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>
    /// Очікуваний тип поля: ключ каталогу + запасне українське слово.
    /// </summary>
    /// <remarks>
    /// ⛔ Той самий дефект, що `U-02` у <see cref="CellValueReader"/>, лише
    /// тонший: ключ тут БУВ (<c>err.ECR-HDR-0422.typeMismatch</c>), але шаблон
    /// підставляв <c>{expected}</c>, а значенням <c>expected</c> їхало
    /// українське слово. Користувач бачив «Header field "QTY" expects a
    /// число.» — англійське речення з українським словом усередині й
    /// неузгодженим артиклем. Резолвер (<c>UiStringResolver.Format</c>)
    /// другого рівня розв'язання ключів не має, тож тип — частина КЛЮЧА.
    ///
    /// ⚠ <see cref="Code"/> їде клієнту полем <c>expected</c> у
    /// <c>problem+json</c> — тому це стале кодове слово, а не текст.
    /// </remarks>
    private sealed record ExpectedType(string Code, string MessageKey, string Fallback)
    {
        public static readonly ExpectedType Number =
            new("Number", "err.ECR-HDR-0422.expectsNumber", "число");

        public static readonly ExpectedType Boolean =
            new("Boolean", "err.ECR-HDR-0422.expectsBoolean", "булеве значення");

        public static readonly ExpectedType Date =
            new("Date", "err.ECR-HDR-0422.expectsDate", "дата");

        public static readonly ExpectedType Identifier =
            new("Identifier", "err.ECR-HDR-0422.expectsIdentifier", "ідентифікатор");
    }

    /// <summary>
    /// Число, яке сховище шапки збереже без втрати; інакше — відмова.
    /// </summary>
    /// <remarks>
    /// ⛔ Той самий дефект, що `U-23` для комірки: <c>doc.DocumentHeaderValue.ValueNumeric</c>
    /// теж <c>decimal(34,16)</c> (<c>DocumentHeaderValueConfiguration</c>), і
    /// зайві знаки SqlClient округлював мовчки. Межа й правило — ті самі
    /// (<see cref="CellValueReader.StorageScale"/>, відмова, а не округлення,
    /// ФВ-9.16c); нулі в хвості втратою не є.
    /// </remarks>
    private static decimal Storable(decimal number, HeaderFieldDef field)
        => !CellValueReader.IntegerPartFits(number)
            ? throw new BusinessRuleException(
                ErrorCodes.HeaderValueInvalid,
                $"Поле шапки «{field.Code}» зберігає не більше {CellValueReader.StorageIntegerDigits} розрядів до коми.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-HDR-0422.tooManyIntegerDigits",
                    ["headerFieldCode"] = field.Code,
                    ["maxIntegerDigits"] = CellValueReader.StorageIntegerDigits.ToString(CultureInfo.InvariantCulture),
                })
            : decimal.Round(number, CellValueReader.StorageScale, MidpointRounding.AwayFromZero) == number
            ? number
            : throw new BusinessRuleException(
                ErrorCodes.HeaderValueInvalid,
                $"Поле шапки «{field.Code}» зберігає не більше {CellValueReader.StorageScale} знаків після коми.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-HDR-0422.tooManyDecimals",
                    ["headerFieldCode"] = field.Code,
                    ["maxScale"] = CellValueReader.StorageScale.ToString(CultureInfo.InvariantCulture),
                });

    private static BusinessRuleException Mismatch(HeaderFieldDef field, object value, ExpectedType expected)
        => new(
            ErrorCodes.HeaderValueInvalid,
            $"Поле шапки «{field.Code}» очікує {expected.Fallback}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = expected.MessageKey,
                ["headerFieldCode"] = field.Code,
                ["expected"] = expected.Code,
                ["actualKind"] = value.GetType().Name,
            });
}
