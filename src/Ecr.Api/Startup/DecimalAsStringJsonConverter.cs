using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Api.Startup;

/// <summary>
/// Пише <see cref="decimal"/> у JSON <b>рядком</b>; читає і рядок, і число.
/// </summary>
/// <remarks>
/// ⛔ JSON-число на клієнті проходить через <c>JSON.parse</c> і стає
/// IEEE-754 <c>double</c> — 15–17 значущих цифр. Для
/// <c>decimal(34,16)</c> це означає, що частина знаків зникає ще до того,
/// як до значення можна дотягнутися: <c>12345.1234567890123456</c>
/// повертається іншим числом, і **мовчки**. Тому контракт
/// (<c>docs/build/02-contracts.md</c> §10, <c>D-30</c>) вимагає рядка.
/// <para>
/// ⚠ Читання лишається терпимим до числа: наявні клієнти й тести надсилають
/// <c>2.5</c>, а не <c>"2.5"</c>, і відмовляти їм означало б зламати запис
/// заради формату відповіді. Сам сервер бере значення з СИРОГО тексту JSON
/// (<see cref="Utf8JsonReader.GetDecimal"/>), тобто число теж не проходить
/// через <c>double</c>.
/// </para>
/// </remarks>
public sealed class DecimalAsStringJsonConverter : JsonConverter<decimal>
{
    /// <inheritdoc />
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DecimalJsonText.Read(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteStringValue(DecimalJsonText.Write(value));
    }

    /// <inheritdoc />
    public override decimal ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DecimalJsonText.Parse(reader.GetString());

    /// <inheritdoc />
    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WritePropertyName(DecimalJsonText.Write(value));
    }
}

/// <summary>
/// Те саме для <c>decimal?</c>: <c>null</c> лишається <c>null</c>, не «"0"».
/// </summary>
/// <remarks>
/// ⚠ Реєструється ЯВНО, хоча <c>System.Text.Json</c> уміє загортати конвертер
/// базового типу сам. Явна реєстрація — щоб поведінка порожнього значення була
/// видима в коді й закріплена тестом: <c>null</c> у відповіді означає «виходу
/// не було», і перетворення його на рядок зробило б із «немає значення»
/// значення.
/// </remarks>
public sealed class NullableDecimalAsStringJsonConverter : JsonConverter<decimal?>
{
    /// <inheritdoc />
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Null ? null : DecimalJsonText.Read(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (value is null)
        {
            writer.WriteNullValue();

            return;
        }

        writer.WriteStringValue(DecimalJsonText.Write(value.Value));
    }
}

/// <summary>Текстове подання <c>decimal</c> у JSON — в одному місці.</summary>
internal static class DecimalJsonText
{
    /// <summary>
    /// Рядок значення без втрати знаків.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>InvariantCulture</c> обов'язковий (<c>CA1305</c>): на локалі з
    /// комою як роздільником сервер віддав би «1,5», і клієнтський
    /// <c>Number()</c> прочитав би з цього <c>NaN</c>.
    /// </remarks>
    /// <param name="value">Значення.</param>
    internal static string Write(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Читає значення з рядка або числа.</summary>
    /// <param name="reader">Читач JSON.</param>
    internal static decimal Read(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.String => Parse(reader.GetString()),

            // ⚠ `GetDecimal` бере цифри з сирого тексту JSON, не з `double`:
            // число в запиті теж доходить без втрати знаків.
            JsonTokenType.Number => reader.GetDecimal(),

            _ => throw new JsonException(
                $"Очікувався рядок або число для decimal, отримано {reader.TokenType}."),
        };

    /// <summary>Розбирає рядок; порожній або нечисловий — помилка формату.</summary>
    /// <param name="text">Текст значення.</param>
    internal static decimal Parse(string? text)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException($"«{text}» не є числом decimal.");
}
