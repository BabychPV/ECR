// src/Ecr.Domain/Services/RegistryKeyNormalizer.cs
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Services;

/// <summary>Частина складеного ключа довідника: тип поля і типізоване значення.</summary>
/// <param name="DataType">Тип поля довідника.</param>
/// <param name="Value">
/// <c>string</c> для <c>String</c>; <c>decimal</c>/<c>long</c>/<c>int</c> для
/// <c>Int</c>/<c>Decimal</c>; <c>bool</c>; <c>DateOnly</c>/<c>DateTime</c>;
/// id цілі (<c>long</c>/<c>int</c>) для <c>Lookup</c> і <c>Unit</c>; <c>null</c> — значення немає.
/// </param>
public readonly record struct RegistryKeyPart(CellDataType DataType, object? Value);

/// <summary>
/// Канонічний рядок і хеш складеного ключа довідника (FEATURE-REGISTRY-TABLES §4.2, <c>D-152</c>).
/// </summary>
/// <remarks>
/// ⛔ Реалізацій рівно дві — ця і клієнтська <c>features/registries/keys/normalizeKey.ts</c>, — і
/// обидві проганяються на ОДНІЙ фікстурі <c>tests/Ecr.TestKit/Fixtures/registry-key-normalization.json</c>.
/// Розбіжність означала б, що клієнт підсвічує дубль, якого сервер не бачить, або навпаки.
///
/// ⚠ «Пробільний» символ тут визначено категоріями Unicode (<c>Cc</c>, <c>Zs</c>, <c>Zl</c>,
/// <c>Zp</c>), а не <c>string.Trim</c>/<c>\s</c>: ці два розходяться між .NET і JavaScript
/// (U+0085 пробільний лише в .NET, U+FEFF — лише в JavaScript). До <c>Cc</c> належить і
/// роздільник частин U+001F, тож усередині текстової частини його бути не може — він стає пробілом,
/// і <c>'a␟b' + 'c'</c> не збігається з <c>'a' + 'b␟c'</c>.
/// </remarks>
public static class RegistryKeyNormalizer
{
    /// <summary>Роздільник частин — U+001F (unit separator).</summary>
    public const char Separator = (char)0x1F;

    /// <summary>
    /// Канонічний рядок ключа: частини в порядку полів ключа, з'єднані <see cref="Separator"/>.
    /// </summary>
    /// <param name="parts">Частини ключа в порядку полів (не порожній перелік).</param>
    /// <param name="ignoreCase">Текст без урахування регістру (типово так, <c>D-152</c>).</param>
    /// <returns>
    /// <c>null</c>, якщо хоч одна частина порожня: такий рядок у перевірку ключа не входить
    /// (<c>D-153</c>, як <c>UNIQUE</c> з різними <c>NULL</c>).
    /// </returns>
    public static string? Canonical(IReadOnlyList<RegistryKeyPart> parts, bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            throw new ArgumentException("A key has at least one part.", nameof(parts));
        }

        var canonical = new string[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var part = NormalizePart(parts[i].DataType, parts[i].Value, ignoreCase);
            if (part is null)
            {
                return null;
            }

            canonical[i] = part;
        }

        return string.Join(Separator, canonical);
    }

    /// <summary>Канонічна форма однієї частини: <c>тег:значення</c>; <c>null</c> — значення немає.</summary>
    /// <param name="dataType">Тип поля.</param>
    /// <param name="value">Значення (див. <see cref="RegistryKeyPart.Value"/>).</param>
    /// <param name="ignoreCase">Текст без урахування регістру.</param>
    public static string? NormalizePart(CellDataType dataType, object? value, bool ignoreCase = true)
    {
        if (value is null)
        {
            return null;
        }

        return dataType switch
        {
            CellDataType.String => Text(value, ignoreCase),
            CellDataType.Int or CellDataType.Decimal => "N:" + Number(value, dataType),
            CellDataType.Bool => value is bool flag ? (flag ? "B:1" : "B:0") : throw Mismatch(dataType, value),
            CellDataType.Date => "D:" + Date(value, dataType),
            CellDataType.Lookup => "L:" + Id(value, dataType),
            CellDataType.Unit => "U:" + Id(value, dataType),
            _ => throw new ArgumentException($"Field type {dataType} cannot be a key part.", nameof(dataType)),
        };
    }

    /// <summary>SHA-256 від UTF-8 канонічного рядка — значення <c>dic.RegistryEntryKey.KeyHash binary(32)</c>.</summary>
    /// <param name="canonical">Результат <see cref="Canonical"/>.</param>
    public static byte[] Hash(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    private static string? Text(object value, bool ignoreCase)
    {
        if (value is not string raw)
        {
            throw Mismatch(CellDataType.String, value);
        }

        // Порядок §4.2: NFC → обрізка → згортання → регістр.
        var text = TrimSpaces(raw.Normalize(NormalizationForm.FormC));
        text = CollapseSpaces(text);

        if (text.Length == 0)
        {
            return null;
        }

        return "S:" + (ignoreCase ? text.ToUpperInvariant() : text);
    }

    private static string TrimSpaces(string text)
    {
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary>Кожна послідовність пробільних символів стає одним <c>' '</c>.</summary>
    private static string CollapseSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inRun = false;
        foreach (var c in text)
        {
            if (IsSpace(c))
            {
                if (!inRun)
                {
                    builder.Append(' ');
                }

                inRun = true;
                continue;
            }

            builder.Append(c);
            inRun = false;
        }

        return builder.ToString();
    }

    private static bool IsSpace(char c) => char.GetUnicodeCategory(c)
        is UnicodeCategory.Control
        or UnicodeCategory.SpaceSeparator
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;

    /// <summary>Інваріантний десятковий без хвостових нулів і без експоненти; <c>-0</c> → <c>0</c>.</summary>
    private static string Number(object value, CellDataType dataType)
    {
        var number = value switch
        {
            decimal d => d,
            long l => l,
            int i => i,
            _ => throw Mismatch(dataType, value),
        };

        if (number == 0m)
        {
            return "0";
        }

        var text = number.ToString(CultureInfo.InvariantCulture);
        return text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    private static string Date(object value, CellDataType dataType)
    {
        var date = value switch
        {
            DateOnly d => d,
            DateTime dt => DateOnly.FromDateTime(dt),
            _ => throw Mismatch(dataType, value),
        };

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string Id(object value, CellDataType dataType) => value switch
    {
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => throw Mismatch(dataType, value),
    };

    private static ArgumentException Mismatch(CellDataType dataType, object value)
        => new($"A value of type {value.GetType().Name} does not fit a {dataType} key part.", nameof(value));
}
