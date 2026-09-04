using System.Globalization;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на **кожен** запит: відкликання ролі має
/// діяти негайно, а не після закінчення cookie (ФВ-6.10, тест безпеки №2).
/// </summary>
public sealed class SecurityStampValidator(EcrDbContext db, IMemoryCache cache, IConfiguration configuration)
{
    /// <summary>
    /// Скільки штамп живе в кеші.
    /// </summary>
    /// <remarks>
    /// П'ять секунд — компроміс, і межа тут не довільна. Без кешу кожен запит
    /// додає звернення до <c>sec.User</c>; із довгим кешем зникає сам сенс
    /// перевірки, бо відкликання ролі почне діяти «колись». П'ять секунд —
    /// це «негайно» з погляду людини і −99% звернень із погляду бази.
    /// </remarks>
    private static readonly TimeSpan DefaultCacheLifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Скільки штамп живе в кеші насправді; налаштовується
    /// <c>Auth:StampCacheSeconds</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Нуль вимикає кеш повністю. Це не «режим для тестів»: контур, де
    /// відкликання має діяти буквально миттєво, платить одним запитом до
    /// sec.User на кожен виклик — і це його право вирішувати.
    /// </remarks>
    private TimeSpan CacheLifetime
        => int.TryParse(
               configuration["Auth:StampCacheSeconds"], CultureInfo.InvariantCulture, out var seconds)
           && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultCacheLifetime;

    /// <summary>Чи актуальний штамп із cookie.</summary>
    public async Task<bool> IsCurrentAsync(int userId, string stampFromCookie, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stampFromCookie))
        {
            return false;
        }

        var lifetime = CacheLifetime;
        var key = $"stamp:{userId}";

        if (lifetime == TimeSpan.Zero || !cache.TryGetValue(key, out string? current))
        {
            current = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == userId && u.IsActive)
                .Select(u => u.SecurityStamp)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (lifetime > TimeSpan.Zero)
            {
                cache.Set(key, current, lifetime);
            }
        }

        // Вимкнений користувач штампа не має — і це теж «не актуальний».
        return current is not null
            && string.Equals(current, stampFromCookie, StringComparison.Ordinal);
    }
}
