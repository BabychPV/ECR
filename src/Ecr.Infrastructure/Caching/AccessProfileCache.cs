using System.Collections.Concurrent;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш профілів доступу. Профіль будується **раз на сесію**: резолвити права
/// на кожну комірку — гарантована смерть продуктивності, бо бюджет відкриття
/// таблиці дає на права 50 мс на весь запит (ФВ-6.10).
/// </summary>
public sealed class AccessProfileCache(IMemoryCache memory, CacheLifetimes? lifetimes = null) : IAccessProfileInvalidator, IDisposable
{
    /// <inheritdoc />
    public void Dispose()
    {
        _all.Dispose();
        foreach (var source in _userTokens.Values)
        {
            source.Dispose();
        }
    }

    /// <summary>Токени записів по користувачу: скасування прибирає ВСІ його записи за будь-якого відбитку груп.</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _userTokens = new();

    /// <summary>Токен «усе»: скасування скидає весь кеш профілів.</summary>
    private CancellationTokenSource _all = new();

    /// <summary>Лічильник скидань: профіль, побудований ПІД ЧАС скидання, не кладеться в кеш застарілим.</summary>
    private long _epoch;

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
    private readonly SingleFlight<Built> _flight = new();

    /// <summary>
    /// Стеля життя запису.
    /// </summary>
    /// <remarks>
    /// Інвалідація тут не потрібна — її робить <c>SecurityStamp</c> у ключі, —
    /// але без стелі запис вимкненого користувача жив би в пам'яті до
    /// перезапуску процесу. Це не про доступ (штамп змінюється), а про пам'ять.
    ///
    /// ⚠ 60 хв — це ДЕФОЛТ (<see cref="CacheLifetimes.DefaultAccessProfileMinutes"/>),
    /// а не константа: значення береться з <c>Cache:AccessProfileSlidingMinutes</c>
    /// (`S-13`; <c>appsettings.json</c> теж 60). Колишні «30 хв» у коментарях застаріли.
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

        // ⛔ fail-closed: скидання не вдалося — кеш міг лишити застарілі профілі, тож
        // їх не читаємо й нових не кладемо, поки InvalidateAll не пройде успішно.
        if (IsDirty)
        {
            Observability.InfrastructureMetrics.RecordCache(Observability.InfrastructureMetrics.AccessProfileCacheName, hit: false);
            return await factory(ct).ConfigureAwait(false);
        }

        if (memory.TryGetValue(key, out AccessProfile? cached) && cached is not null)
        {
            Observability.InfrastructureMetrics.RecordCache(Observability.InfrastructureMetrics.AccessProfileCacheName, hit: true);
            return cached;
        }

        Observability.InfrastructureMetrics.RecordCache(Observability.InfrastructureMetrics.AccessProfileCacheName, hit: false);

        // ⛔ Вхід у систему сотні людей о 9:00 — це сотня промахів на РІЗНИХ
        // ключах, але одна людина з десятком вкладок дає десяток промахів на
        // ОДНОМУ, і кожен будував профіль окремо (`RD-05`).
        //
        // ⚠ Той, хто приєднався до вже запущеного польоту, міг би отримати профіль,
        // побудований ДО скидання (до коміту гранта). Тому профіль несе епоху, під
        // якою будувався; якщо скидання сталося — одна перебудова (друга спроба — остання).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var built = await _flight.RunAsync(key, token => BuildAsync(userId, key, factory, token), ct)
                .ConfigureAwait(false);
            if (built.Epoch == Interlocked.Read(ref _epoch))
            {
                return built.Profile;
            }
        }

        // ⛔ ent7 P3-5: скидання сталося й під час другого польоту — приєднаний міг би отримати профіль
        // без нової заборони. Будуємо поза польотом (у кеш не кладемо), а не повертаємо «останній» політ.
        return await factory(ct).ConfigureAwait(false);
    }

    /// <summary>Профіль разом з епохою скидань, під якою його побудовано.</summary>
    private readonly record struct Built(AccessProfile Profile, long Epoch);

    /// <summary>Будує профіль і кладе його в кеш — усередині одного польоту.</summary>
    private async Task<Built> BuildAsync(
        int userId, string key, Func<CancellationToken, Task<AccessProfile>> factory, CancellationToken ct)
    {
        if (memory.TryGetValue(key, out AccessProfile? ready) && ready is not null)
        {
            return new Built(ready, Interlocked.Read(ref _epoch));
        }

        var epoch = Interlocked.Read(ref _epoch);
        var all = _all;
        var userToken = _userTokens.GetOrAdd(userId, _ => new CancellationTokenSource());
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var profile = await factory(ct).ConfigureAwait(false);
        Observability.InfrastructureMetrics.RecordAccessProfileBuild(
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);

        // ⛔ Профіль симуляції в кеш не потрапляє НІКОЛИ (ФВ-6.16a п. 4).
        // Ключ складається з користувача і штампа — тобто профіль суб'єкта ліг
        // би під ключ, за яким справжній користувач дістав би чужі права разом
        // із прапорцем IsSimulation. Симуляція рідкісна, перебудова дешева.
        // Скидання, що сталося, поки профіль будувався, робить його можливо застарілим: віддаємо, але не кешуємо.
        if (!profile.IsSimulation && Interlocked.Read(ref _epoch) == epoch && !IsDirty)
        {
            var options = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime };
            options.AddExpirationToken(new CancellationChangeToken(all.Token));
            options.AddExpirationToken(new CancellationChangeToken(userToken.Token));
            memory.Set(key, profile, options);
        }

        return new Built(profile, epoch);
    }

    /// <inheritdoc />
    public void MarkInvalidationFailed()
    {
        Interlocked.Increment(ref _failGen);
        Observability.InfrastructureMetrics.RecordAccessProfileInvalidationFailure();
    }

    /// <summary>Лічильник збоїв скидання; кеш «брудний», поки <see cref="_cleanGen"/> його не наздогнав.</summary>
    private long _failGen;

    /// <summary>Найбільший <see cref="_failGen"/>, прочитаний на СТАРТІ успішного <see cref="InvalidateAll"/>.</summary>
    private long _cleanGen;

    /// <summary>Скидання не вдалося: кеш не читається й не наповнюється до успішного <see cref="InvalidateAll"/>.</summary>
    private bool IsDirty => Volatile.Read(ref _failGen) != Volatile.Read(ref _cleanGen);

    /// <inheritdoc />
    public void InvalidateUser(int userId)
    {
        Interlocked.Increment(ref _epoch);
        if (_userTokens.TryRemove(userId, out var source))
        {
            source.Cancel(); // не Dispose: паралельна побудова ще може взяти source.Token
        }
    }

    /// <inheritdoc />
    public void InvalidateAll()
    {
        // Збій, зареєстрований ПІСЛЯ старту цього скидання, не знімається ним: знімаємо лише
        // збої, що були до старту (скидання, яке почалося раніше, могло їх не покрити).
        var gen = Volatile.Read(ref _failGen);
        Interlocked.Increment(ref _epoch);
        var old = Interlocked.Exchange(ref _all, new CancellationTokenSource());
        old.Cancel();
        long seen;
        while ((seen = Volatile.Read(ref _cleanGen)) < gen
               && Interlocked.CompareExchange(ref _cleanGen, gen, seen) != seen)
        {
        } // лише повне скидання знімає прапорець
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
