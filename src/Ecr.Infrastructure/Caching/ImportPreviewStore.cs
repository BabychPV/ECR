using System.Text;
using Ecr.Application.Ports;
using Microsoft.Extensions.Caching.Distributed;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Diff-и імпорту в розподіленому кеші.
/// </summary>
/// <remarks>
/// ⚠ Саме розподілений, а не <c>IMemoryCache</c>. Перегляд і застосування —
/// це два HTTP-запити, і другий цілком може потрапити на інший інстанс
/// застосунку: у памʼяті процесу diff знаходився б лише тоді, коли пощастило,
/// і «застосувати» ламалося б випадково — найгірший вид поломки, бо він
/// відтворюється в одному запуску з десяти.
/// <para>
/// Кеш у продуктиві лежить у SQL Server (<c>Microsoft.Extensions.Caching.SqlServer</c>),
/// тобто переживає перезапуск і не потребує окремої інфраструктури.
/// </para>
/// </remarks>
public sealed class ImportPreviewStore(IDistributedCache cache) : IImportPreviewStore
{
    /// <summary>Префікс ключа — щоб diff-и не змішалися з рештою кешу.</summary>
    public const string KeyPrefix = "ecr:import-preview:";

    /// <inheritdoc />
    public Task SaveAsync(string token, string payloadJson, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        // ⛔ Абсолютний строк, а не ковзний. Ковзний продовжувався б від
        // кожного перегляду сторінки, і diff, побудований учора, лишався б
        // застосовним доти, доки вкладку тримають відкритою.
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime };

        return cache.SetAsync(Key(token), Encoding.UTF8.GetBytes(payloadJson), options, ct);
    }

    /// <inheritdoc />
    public async Task<string?> FindAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var bytes = await cache.GetAsync(Key(token), ct).ConfigureAwait(false);

        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string token, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return cache.RemoveAsync(Key(token), ct);
    }

    private static string Key(string token) => KeyPrefix + token;
}
