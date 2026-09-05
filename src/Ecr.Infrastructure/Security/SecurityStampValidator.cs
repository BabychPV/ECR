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
    /// <param name="userId">Користувач із заявки.</param>
    /// <param name="stampFromCookie">Штамп, записаний у cookie при вході.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Кеш тут **тільки пришвидшує згоду і ніколи не дає відмови**. Різниця
    /// принципова, і до `A7-21` її не було: кеш зберігає ШТАМП, а зміна пароля
    /// його прокручує. Одразу після зміни в кеші лежало старе значення, а в
    /// щойно виданій cookie — нове; вони не збігалися, і застосунок виходив із
    /// сеансу, який сам щойно створив. Користувач потрапляв на форму входу,
    /// успішно входив і за наступним запитом опинявся там знову — і так усі
    /// п'ять секунд.
    ///
    /// ⚠ Тому розбіжність не є вироком: вона коштує ОДНОГО читання
    /// <c>sec.User</c>, після якого рішення ухвалюється за фактом. Дешевий шлях
    /// лишається дешевим (збіг — без бази), а дорогий трапляється рівно тоді,
    /// коли ціна помилки максимальна.
    ///
    /// ⚠ Вікно відкликання від цього не подовжується. Відкликана роль дає
    /// протилежну картину: у кеші СТАРИЙ штамп, у cookie той самий старий, вони
    /// збігаються — і доступ живе задокументовані п'ять секунд, як і задумано.
    /// </remarks>
    public async Task<bool> IsCurrentAsync(int userId, string stampFromCookie, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(stampFromCookie))
        {
            return false;
        }

        var lifetime = CacheLifetime;
        var key = $"stamp:{userId}";

        if (lifetime > TimeSpan.Zero
            && cache.TryGetValue(key, out string? cached)
            && string.Equals(cached, stampFromCookie, StringComparison.Ordinal))
        {
            return true;
        }

        var current = await ReadStampAsync(userId, ct).ConfigureAwait(false);

        if (lifetime > TimeSpan.Zero)
        {
            cache.Set(key, current, lifetime);
        }

        // Вимкнений користувач штампа не має — і це теж «не актуальний».
        return current is not null
            && string.Equals(current, stampFromCookie, StringComparison.Ordinal);
    }

    /// <summary>Читає чинний штамп активного користувача.</summary>
    private async Task<string?> ReadStampAsync(int userId, CancellationToken ct)
        => await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId && u.IsActive)
            .Select(u => u.SecurityStamp)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
