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

    /// <summary>
    /// Точково видаляє запис профілю для конкретного (користувач, штамп), НЕ
    /// чіпаючи сам штамп.
    /// </summary>
    /// <remarks>
    /// ⛔ На відміну від зміни ролі (де штамп крутиться навмисно й негайно
    /// розлоговує сесію — ФВ-6.7), тут мета протилежна: дати ЦІЙ ЖЕ сесії
    /// побачити свіжий грант, не чіпаючи автентифікацію. Ключ сесії й ключ
    /// кешу профілю — обидва «користувач + штамп», але сесія звіряє штамп у
    /// cookie, а кеш — лише в <see cref="IMemoryCache"/>; видалення другого
    /// без зміни першого не зачіпає сесію взагалі.
    /// </remarks>
    /// <param name="userId">Користувач, чий профіль застарів.</param>
    /// <param name="securityStamp">Поточний штамп користувача (без фінгерпринта груп).</param>
    public void Evict(int userId, string securityStamp)
        => memory.Remove(Key(userId, securityStamp));

    /// <summary>Ключ запису; виділений, щоб форма ключа була в одному місці.</summary>
    /// <summary>
    /// Ключ профілю.
    /// </summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="securityStamp">Штамп безпеки: зміна ролей робить сесію недійсною негайно.</param>
    /// <param name="groupsFingerprint">
    /// Відбиток груп із токена; порожньо — профіль будувався без групових
    /// призначень.
    /// </param>
    /// <remarks>
    /// ⚠ Відбиток груп входить у ключ обов'язково (`P-02`). Той самий
    /// користувач має РІЗНІ профілі залежно від того, чи є в нас його токен:
    /// власна сесія бачить ролі, призначені на AD-групу, а перегляд
    /// адміністратором і симуляція — ні. Спільний ключ віддав би одному з них
    /// профіль другого.
    /// </remarks>
    public static string Key(int userId, string securityStamp, string groupsFingerprint = "")
        => $"access:{userId}:{securityStamp}:{groupsFingerprint}";
}
