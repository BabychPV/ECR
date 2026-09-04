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

        var built = await factory(ct).ConfigureAwait(false);

        memory.Set(key, built, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Lifetime,

            // Розмір у записах списку, а не в байтах: кеш обмежується кількістю
            // (див. налаштування MemoryCache), і довідник на 5 000 записів має
            // важити стільки ж, скільки 5 000 дрібних, а не стільки ж, скільки один.
            Size = built.Count + 1,
        });

        return built;
    }
}
