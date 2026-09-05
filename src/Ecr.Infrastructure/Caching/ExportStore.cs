using Ecr.Application.Ports;
using Microsoft.Extensions.Caching.Distributed;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Готові книги експорту в розподіленому кеші.
/// </summary>
/// <remarks>
/// ⚠ Те саме сховище, що й у diff-ів імпорту, і з тієї самої причини: між
/// побудовою і забиранням минає час, і другий запит цілком може потрапити на
/// інший інстанс застосунку.
///
/// ⚠ Книга на 500×60×12 — це одиниці мегабайтів. Класти її в кеш прийнятно
/// саме тому, що строк життя короткий: година, а не доба.
/// </remarks>
public sealed class ExportStore(IDistributedCache cache) : IExportStore
{
    /// <summary>Префікс ключа — щоб книги не змішалися з рештою кешу.</summary>
    public const string KeyPrefix = "ecr:export:";

    /// <inheritdoc />
    public Task SaveAsync(string exportId, byte[] content, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);
        ArgumentNullException.ThrowIfNull(content);

        // ⛔ Абсолютний строк, а не ковзний: книга не має «продовжувати життя»
        // від того, що її кілька разів завантажили.
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = lifetime };

        return cache.SetAsync(KeyPrefix + exportId, content, options, ct);
    }

    /// <inheritdoc />
    public Task<byte[]?> FindAsync(string exportId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportId);

        return cache.GetAsync(KeyPrefix + exportId, ct);
    }
}
