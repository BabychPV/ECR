// src/Ecr.Domain/ValueObjects/LocalizedText.cs

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Локалізований текст. Додати мову = додати запис у <c>sys.Language</c>, а не
/// колонку в двадцяти таблицях (ФВ-2.2). Два формати JSON — не плутати:
/// колонка БД <c>…L10n</c> (<c>ToJson</c>/<c>FromJson</c>) зберігає плаский словник
/// <c>{"en":"…","ru":"…","kz":"…"}</c>; JSON DTO — тіло запиту й відповіді API
/// (серіалізація самого об'єкта: властивість <c>Values</c>) — обгортка
/// <c>{"values":{"en":"…","ru":"…","kz":"…"}}</c>.
/// </summary>
public sealed class LocalizedText
{
    private readonly Dictionary<string, string> _values;

    // ⛔ Q-301: параметр конструктора мусить збігатися ТИПОМ (не лише іменем) з
    // публічною властивістю Values нижче — System.Text.Json зіставляє
    // [JsonConstructor]-параметр із публічною властивістю за іменем без
    // урахування регістру і кидає InvalidOperationException при першому ж
    // використанні типу, якщо типи різні (тут було Dictionary<...> проти
    // IReadOnlyDictionary<...>) — ламало БУДЬ-яку десеріалізацію через MVC,
    // не лише випадок із непорожнім значенням.
    [JsonConstructor]
    public LocalizedText(IReadOnlyDictionary<string, string>? values = null)
        => _values = values is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

    /// <summary>Значення для мови; якщо немає — для <paramref name="fallback"/>; якщо і його немає — перше наявне.</summary>
    public string? Get(string language, string fallback = "en")
    {
        if (_values.TryGetValue(language, out var v))
        {
            return v;
        }
        if (_values.TryGetValue(fallback, out var f))
        {
            return f;
        }
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
