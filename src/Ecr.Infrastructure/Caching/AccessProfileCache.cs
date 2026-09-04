using Ecr.Application.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш профілів доступу. Профіль будується **раз на сесію**: резолвити права
/// на кожну комірку — гарантована смерть продуктивності, бо бюджет відкриття
/// таблиці дає на права 50 мс на весь запит (ФВ-6.10).
/// </summary>
public sealed class AccessProfileCache(IMemoryCache memory)
{
    /// <summary>
    /// Стеля життя запису.
    /// </summary>
    /// <remarks>
    /// Інвалідація тут не потрібна — її робить <c>SecurityStamp</c> у ключі, —
    /// але без стелі запис вимкненого користувача жив би в пам'яті до
    /// перезапуску процесу. Це не про доступ (штамп змінюється), а про пам'ять.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>Повертає профіль із кешу або будує його.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="securityStamp">Штамп безпеки — частина ключа.</param>
    /// <param name="factory">Побудова профілю при промаху.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<AccessProfile> GetOrCreateAsync(
        int userId, string securityStamp, Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentNullException.ThrowIfNull(factory);

        // Зміна ролей або пароля змінює SecurityStamp, тому старий запис просто
        // перестає використовуватися — явна інвалідація не потрібна.
        var key = Key(userId, securityStamp);
        if (memory.TryGetValue(key, out AccessProfile? cached) && cached is not null)
        {
            return cached;
        }

        var profile = await factory(ct).ConfigureAwait(false);

        // ⛔ Профіль симуляції в кеш не потрапляє НІКОЛИ (ФВ-6.16a п. 4).
        // Ключ складається з користувача і штампа — тобто профіль суб'єкта ліг
        // би під ключ, за яким справжній користувач дістав би чужі права разом
        // із прапорцем IsSimulation. Симуляція рідкісна, перебудова дешева.
        if (!profile.IsSimulation)
        {
            memory.Set(key, profile, Lifetime);
        }

        return profile;
    }

    /// <summary>Ключ запису; виділений, щоб форма ключа була в одному місці.</summary>
    public static string Key(int userId, string securityStamp) => $"access:{userId}:{securityStamp}";
}
