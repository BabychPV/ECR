// src/Ecr.Domain/ValueObjects/LocalizedText.cs
namespace Ecr.Domain.ValueObjects;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Локалізований текст. Зберігається однією колонкою <c>…L10n</c> у форматі JSON
/// <c>{"en":"…","ru":"…","kz":"…"}</c>. Додати мову = додати запис у
/// <c>sys.Language</c>, а не колонку в двадцяти таблицях (ФВ-2.2).
/// </summary>
public sealed class LocalizedText
{
    private readonly Dictionary<string, string> _values;

    [JsonConstructor]
    public LocalizedText(Dictionary<string, string>? values = null)
        => _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Значення для мови; якщо немає — для <paramref name="fallback"/>; якщо і його немає — перше наявне.</summary>
    public string? Get(string language, string fallback = "en")
    {
        if (_values.TryGetValue(language, out var v)) return v;
        if (_values.TryGetValue(fallback, out var f)) return f;
        return _values.Count > 0 ? _values.Values.First() : null;
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Серіалізація у формат зберігання.</summary>
    public string ToJson() => JsonSerializer.Serialize(_values);

    /// <summary>Десеріалізація зі стовпця <c>…L10n</c>.</summary>
    public static LocalizedText FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new LocalizedText()
            : new LocalizedText(JsonSerializer.Deserialize<Dictionary<string, string>>(json));
}
