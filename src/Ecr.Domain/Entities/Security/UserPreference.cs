namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Одне налаштування інтерфейсу користувача (<c>sec.UserPreference</c>, <c>BE-20</c>):
/// ключ → JSON-значення. Пара <c>(UserId, Key)</c> і є ідентичністю.
/// </summary>
/// <remarks>
/// Ключ-значення, а не колонка на налаштування: нове налаштування клієнта не
/// вимагає міграції. Правила ключа й розміру — у <c>UserPreferenceRules</c>.
/// </remarks>
public sealed class UserPreference
{
    /// <summary>Найбільша довжина ключа; та сама, що в стовпці.</summary>
    public const int KeyMaxLength = 100;

    private UserPreference() { }

    /// <summary>Створює налаштування.</summary>
    public UserPreference(int userId, string key, string valueJson, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(key.Length, KeyMaxLength);

        UserId = userId;
        Key = key;
        Replace(valueJson, utcNow);
    }

    public int UserId { get; private set; }
    public string Key { get; private set; } = null!;
    public string ValueJson { get; private set; } = null!;
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Замінює значення (остання запис перемагає: налаштування лише власні).</summary>
    public void Replace(string valueJson, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueJson);

        ValueJson = valueJson;
        UpdatedAt = utcNow;
    }
}
