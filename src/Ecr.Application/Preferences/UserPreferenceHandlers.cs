// src/Ecr.Application/Preferences/UserPreferenceHandlers.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Errors;

namespace Ecr.Application.Preferences;

/// <summary>Одне налаштування у відповіді API.</summary>
/// <param name="Key">Ключ.</param>
/// <param name="Value">Значення — довільний JSON, який поклав клієнт.</param>
/// <param name="UpdatedAt">Коли змінено востаннє, UTC.</param>
public sealed record UserPreferenceDto(string Key, JsonElement Value, DateTime UpdatedAt);

/// <summary>Правила ключа й значення налаштування (<c>BE-20</c>).</summary>
public static partial class UserPreferenceRules
{
    /// <summary>Найбільший розмір значення в байтах UTF-8.</summary>
    public const int MaxValueBytes = 8 * 1024;

    /// <summary>Найбільша кількість налаштувань одного користувача.</summary>
    public const int MaxPerUser = 200;

    /// <summary>Ключі без підключів: налаштування директиви BE-20.</summary>
    public static readonly IReadOnlySet<string> ExactKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "theme", "density", "language", "lastPeriodKey", "lastProjectId",
    };

    /// <summary>Простори з підключем: <c>grid.columnWidths.{tableInstanceId}</c> тощо.</summary>
    public static readonly IReadOnlyList<string> Prefixes = ["grid."];

    /// <summary>Чи допустимий ключ: формат, довжина і білий список.</summary>
    public static bool IsKeyAllowed(string? key)
        => key is { Length: > 0 and <= UserPreference.KeyMaxLength }
           && KeyFormat().IsMatch(key)
           && (ExactKeys.Contains(key) || Prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)));

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\.[A-Za-z0-9_-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyFormat();

    internal static string RequireKey(string? key)
    {
        if (!IsKeyAllowed(key))
        {
            throw Invalid("Недопустимий ключ налаштування.", "err.ECR-REQ-0422.preferenceKeyInvalid", key, new()
            {
                ["max"] = UserPreference.KeyMaxLength.ToString(CultureInfo.InvariantCulture),
            });
        }

        return key!;
    }

    // Маршрути під [Authorize]: анонім сюди не доходить, тож це дефект, не 401.
    internal static int RequireUser(ICurrentUser currentUser)
        => currentUser.UserId
           ?? throw new InvalidOperationException("Налаштування без користувача сеансу.");

    internal static BusinessRuleException Invalid(
        string message, string messageKey, string? key, Dictionary<string, object?>? extra = null)
    {
        var details = extra ?? [];
        details["messageKey"] = messageKey;
        details["key"] = key;
        return new BusinessRuleException(ErrorCodes.RequestInvalid, message, details);
    }

    internal static UserPreferenceDto ToDto(UserPreference p)
    {
        using var doc = JsonDocument.Parse(p.ValueJson);
        return new UserPreferenceDto(p.Key, doc.RootElement.Clone(), p.UpdatedAt);
    }
}

/// <summary>Усі налаштування поточного користувача.</summary>
public sealed class ListUserPreferencesHandler(IUserPreferenceStore store, ICurrentUser currentUser)
{
    /// <summary>Повертає налаштування, упорядковані за ключем.</summary>
    public async Task<IReadOnlyList<UserPreferenceDto>> HandleAsync(CancellationToken ct)
    {
        var userId = UserPreferenceRules.RequireUser(currentUser);
        var items = await store.ListAsync(userId, ct).ConfigureAwait(false);
        return [.. items.Select(UserPreferenceRules.ToDto)];
    }
}

/// <summary>Записує (upsert) одне налаштування поточного користувача.</summary>
public sealed class PutUserPreferenceHandler(
    IUserPreferenceStore store, IUnitOfWork uow, ICurrentUser currentUser, IClock clock)
{
    /// <summary>Створює або замінює значення за ключем.</summary>
    /// <param name="key">Ключ із білого списку.</param>
    /// <param name="valueJson">Сире JSON-значення; <c>null</c> — тіла немає.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-REQ-0422</c>: ключ, значення, розмір або кількість.</exception>
    public async Task<UserPreferenceDto> HandleAsync(string key, string? valueJson, CancellationToken ct)
    {
        var userId = UserPreferenceRules.RequireUser(currentUser);
        key = UserPreferenceRules.RequireKey(key);

        var normalized = Normalize(key, valueJson);
        var size = Encoding.UTF8.GetByteCount(normalized);
        if (size > UserPreferenceRules.MaxValueBytes)
        {
            throw UserPreferenceRules.Invalid("Значення налаштування завелике.", "err.ECR-REQ-0422.preferenceValueTooLarge", key, new()
            {
                ["size"] = size.ToString(CultureInfo.InvariantCulture),
                ["max"] = UserPreferenceRules.MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            });
        }

        var existing = await store.FindAsync(userId, key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            var count = await store.CountAsync(userId, ct).ConfigureAwait(false);
            if (count >= UserPreferenceRules.MaxPerUser)
            {
                throw UserPreferenceRules.Invalid("Забагато налаштувань.", "err.ECR-REQ-0422.preferenceLimitReached", key, new()
                {
                    ["max"] = UserPreferenceRules.MaxPerUser.ToString(CultureInfo.InvariantCulture),
                });
            }

            existing = new UserPreference(userId, key, normalized, clock.UtcNow);
            store.Add(existing);
        }
        else
        {
            existing.Replace(normalized, clock.UtcNow);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return UserPreferenceRules.ToDto(existing);
    }

    // Перезапис без пробілів: ліміт міряє дані, а не форматування клієнта.
    private static string Normalize(string key, string? valueJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueJson ?? string.Empty);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException)
        {
            throw UserPreferenceRules.Invalid("Значення налаштування — не JSON.", "err.ECR-REQ-0422.preferenceValueInvalid", key);
        }
    }
}

/// <summary>Видаляє налаштування поточного користувача; відсутнє — не помилка.</summary>
public sealed class DeleteUserPreferenceHandler(IUserPreferenceStore store, IUnitOfWork uow, ICurrentUser currentUser)
{
    /// <summary>Видаляє налаштування за ключем (ідемпотентно).</summary>
    public async Task HandleAsync(string key, CancellationToken ct)
    {
        var userId = UserPreferenceRules.RequireUser(currentUser);
        key = UserPreferenceRules.RequireKey(key);

        var existing = await store.FindAsync(userId, key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return;
        }

        store.Remove(existing);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
