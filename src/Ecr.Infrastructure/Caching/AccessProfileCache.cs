using Ecr.Application.Security;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш профілів доступу. Профіль будується **раз на сесію**: резолвити права
/// на кожну комірку — гарантована смерть продуктивності, бо бюджет відкриття
/// таблиці дає на права 50 мс на весь запит (ФВ-6.10).
/// </summary>
public sealed class AccessProfileCache(IMemoryCache memory, CacheLifetimes? lifetimes = null)
{
    /// <summary>
    /// Один політ на ключ (`RD-05`).
    /// </summary>
    /// <remarks>
    /// ⚠ Поле, а не параметр: сам кеш — <c>Singleton</c> (див.
    /// <c>DependencyInjection</c>), тож словник і так спільний на процес.
    ///
    /// ⛔ Чому ділити ПОБУДОВУ профілю безпечно. Ключ складається з
    /// (користувач, штамп, відбиток груп), а фабрика в єдиного викликача —
    /// <c>AccessDecisionService.BuildProfileAsync</c> — будує профіль рівно з
    /// цих самих трьох величин. Отже два одночасні промахи на один ключ
    /// будують ТОТОЖНІ профілі, і віддати їм спільний результат — не те саме,
    /// що сплутати профілі (`Q-187`). Профіль симуляції сюди не приходить
    /// узагалі: <c>SimulationService</c> будує його поза кешем і з іншим
    /// <c>CacheKey</c>; перевірка нижче лишається другим рубежем.
    /// </remarks>
    private readonly SingleFlight<AccessProfile> _flight = new();

    /// <summary>
    /// Стеля життя запису.
    /// </summary>
    /// <remarks>
    /// Інвалідація тут не потрібна — її робить <c>SecurityStamp</c> у ключі, —
    /// але без стелі запис вимкненого користувача жив би в пам'яті до
    /// перезапуску процесу. Це не про доступ (штамп змінюється), а про пам'ять.
    ///
    /// ⚠ 30 хв — це ДЕФОЛТ, а не константа: значення береться з
    /// <c>Cache:AccessProfileSlidingMinutes</c> (`S-13`). Ключ був у
    /// <c>appsettings.json</c> без читача, тобто виставлені там 60 хв не діяли.
    /// </remarks>
    private TimeSpan Lifetime => (lifetimes ?? CacheLifetimes.Default).AccessProfile;

    /// <summary>Повертає профіль із кешу або будує його.</summary>
    /// <param name="userId">Користувач.</param>
    /// <param name="securityStamp">Штамп безпеки — частина ключа.</param>
    /// <param name="groupsFingerprint">
    /// Відбиток груп, з якими буде побудований профіль при промаху
    /// (<see cref="Key"/>) — ОБОВ'ЯЗКОВИЙ параметр, а не з дефолтом
    /// (`Q-187`): саме забутий тут третій аргумент дав змогу профілю
    /// власної сесії (з групами) і профілю перегляду адміністратором /
    /// симуляції (без груп) того самого користувача лягати в ОДИН запис
    /// `IMemoryCache`, хоча значення `AccessProfile.CacheKey` вже рахувало
    /// цей відбиток — просто не як реальний ключ пошуку.
    /// </param>
    /// <param name="factory">Побудова профілю при промаху.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<AccessProfile> GetOrCreateAsync(
        int userId, string securityStamp, string groupsFingerprint,
        Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentNullException.ThrowIfNull(groupsFingerprint);
        ArgumentNullException.ThrowIfNull(factory);

        // Зміна ролей або пароля змінює SecurityStamp, тому старий запис просто
        // перестає використовуватися — явна інвалідація не потрібна.
        var key = Key(userId, securityStamp, groupsFingerprint);
        if (memory.TryGetValue(key, out AccessProfile? cached) && cached is not null)
        {
            return cached;
        }

        // ⛔ Вхід у систему сотні людей о 9:00 — це сотня промахів на РІЗНИХ
        // ключах, але одна людина з десятком вкладок дає десяток промахів на
        // ОДНОМУ, і кожен будував профіль окремо (`RD-05`).
        return await _flight.RunAsync(key, token => BuildAsync(key, factory, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Будує профіль і кладе його в кеш — усередині одного польоту.</summary>
    private async Task<AccessProfile> BuildAsync(
        string key, Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
    {
        if (memory.TryGetValue(key, out AccessProfile? ready) && ready is not null)
        {
            return ready;
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
    /// <param name="securityStamp">Поточний штамп користувача.</param>
    /// <param name="groupsFingerprint">
    /// Відбиток груп ТОГО САМОГО запису, що клав <see cref="GetOrCreateAsync"/>
    /// (`Q-187`) — без нього скидання цілило б у порожній варіант ключа,
    /// а реальний запис (із групами) лишався б неторканим до сплину TTL.
    /// </param>
    public void Evict(int userId, string securityStamp, string groupsFingerprint)
        => memory.Remove(Key(userId, securityStamp, groupsFingerprint));

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
    // ⛔ Q-222 (аудит): без параметра за замовчуванням — коментар вище прямо
    // каже "ОБОВ'ЯЗКОВО" (P-02, той самий інваріант, що Q-187 уже виправляв
    // як безпековий дефект), а необов'язковий параметр дозволяв БУДЬ-ЯКОМУ
    // майбутньому виклику мовчки його пропустити й отримати чужий профіль із
    // кешу. Усі три чинні виклики й так передають його явно — це нічого не
    // ламає, лише закриває шлях для нового виклику, що забув.
    public static string Key(int userId, string securityStamp, string groupsFingerprint)
        => $"access:{userId}:{securityStamp}:{groupsFingerprint}";
}
