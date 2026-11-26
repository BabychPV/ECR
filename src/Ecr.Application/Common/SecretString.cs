// src/Ecr.Application/Common/SecretString.cs
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Application.Common;

/// <summary>
/// Секрет, що прийшов у тілі запиту (пароль джерела, підтвердження секрету): у JSON — звичайний рядок, у пам'яті —
/// обгортка, яка не друкує значення (P-1126-SRCCRED, R-12).
/// </summary>
/// <remarks>
/// ⛔ C#-record генерує <c>ToString()</c>/<c>PrintMembers</c> з УСІМА властивостями: будь-який <c>Log(request)</c>,
/// рядок винятку з аргументами чи вікно налагоджувача виводили б пароль. Обгортка друкує <c>***</c> — і жодної
/// довжини. Значення дістається лише явним <see cref="Reveal"/> у точці, де воно справді потрібне.
/// <para>
/// ⚠ Порожній рядок у JSON читається як <c>null</c>: «пароль не введено» має одну форму (як <c>Password</c> у SMTP:
/// порожнє = не змінювати).
/// </para>
/// </remarks>
[JsonConverter(typeof(SecretStringJsonConverter))]
[DebuggerDisplay("***")]
public sealed class SecretString
{
    /// <summary>Що друкується замість значення.</summary>
    public const string Mask = "***";

    private readonly string value;

    /// <summary>Обгортає непорожнє значення.</summary>
    /// <param name="value">Секрет.</param>
    public SecretString(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        this.value = value;
    }

    /// <summary>Значення — лише там, де його справді треба передати далі (шифрування, рядок драйвера).</summary>
    public string Reveal() => value;

    /// <inheritdoc />
    public override string ToString() => Mask;

    /// <summary>Обгортка над рядком; порожній або <c>null</c> — <c>null</c>.</summary>
    /// <param name="value">Секрет або нічого.</param>
    public static SecretString? From(string? value) => string.IsNullOrEmpty(value) ? null : new SecretString(value);
}

/// <summary>
/// JSON-форма <see cref="SecretString"/>: читається звичайним рядком; пишеться маскою — значення не виїжджає назад навіть
/// у разі випадкової серіалізації запиту.
/// </summary>
public sealed class SecretStringJsonConverter : JsonConverter<SecretString>
{
    /// <inheritdoc />
    public override bool HandleNull => false;

    /// <inheritdoc />
    public override SecretString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Секрет має бути рядком.");
        }

        return SecretString.From(reader.GetString());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, SecretString value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(SecretString.Mask);
    }
}
