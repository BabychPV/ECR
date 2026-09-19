using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Реалізація <see cref="IRegistryEntryCache"/> над <see cref="IMemoryCache"/>.
/// </summary>
/// <remarks>
/// Ключ уже несе <c>DataRevision</c> довідника і дату резолвінгу, тому явної
/// інвалідації немає — так само, як із метаданими версії шаблону (D-16).
/// Зміна запису піднімає ревізію, і наступний запит просто йде за іншим
/// ключем; старий доживає своє і зникає.
/// </remarks>
public sealed class RegistryEntryCache(IMemoryCache memory) : IRegistryEntryCache
{
    /// <summary>
    /// Стеля життя запису.
    /// </summary>
    /// <remarks>
    /// Потрібна не для актуальності — її тримає ревізія в ключі, — а для
    /// пам'яті: без стелі кожна дата резолвінгу лишала б у процесі власний
    /// список назавжди, а дат за рік — 365 на кожен довідник.
    /// </remarks>
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Один політ на ключ (`RD-05`).
    /// </summary>
    /// <remarks>
    /// Кеш — <c>Singleton</c>, тож словник спільний на процес. Довідник на
    /// 50 000 записів (`RD-06`) читається одним запитом, але великим: N
    /// одночасних промахів на один ключ означали N таких читань.
    /// </remarks>
    private readonly SingleFlight<IReadOnlyList<RegistryEntry>> _flight = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntry>> GetOrAddAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>> factory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (memory.TryGetValue(key, out IReadOnlyList<RegistryEntry>? cached) && cached is not null)
        {
            return cached;
        }

        return await _flight.RunAsync(key, token => BuildAsync(key, factory, token), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Читає довідник і кладе його в кеш — усередині одного польоту.</summary>
    private async Task<IReadOnlyList<RegistryEntry>> BuildAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>> factory,
        CancellationToken ct)
    {
        if (memory.TryGetValue(key, out IReadOnlyList<RegistryEntry>? ready) && ready is not null)
        {
            return ready;
        }

        var built = await factory(ct).ConfigureAwait(false);

        // ⚠ `Size` прибрано разом із коментарем про «кеш обмежується
        // кількістю»: ліміту в сховища немає і не буде в межах цього кроку
        // (пояснення — біля `AddMemoryCache` у `DependencyInjection.cs`), а
        // розмір без ліміту не обмежує нічого. Стелю тут тримає строк.
        memory.Set(key, built, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,
        });

        return built;
    }
}
