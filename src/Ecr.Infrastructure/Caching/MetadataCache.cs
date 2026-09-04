using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Ecr.Infrastructure.Persistence;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// Кеш метаданих шаблону.
/// </summary>
/// <remarks>
/// Ключ <c>v{id}:r{rev}</c> робить інвалідацію **непотрібною**: презентаційна
/// правка створює новий ключ, а не псує старий. Це прибирає когерентність кешу
/// між інстансами як клас проблеми — і саме тому ≥2 інстанси тут дешеві (D-16).
/// </remarks>
public sealed class MetadataCache(IMemoryCache memory, EcrDbContext db) : IMetadataCache
{
    /// <inheritdoc />
    public Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прочитати PresentationRevision одним легким запитом; ключ = $\"v{id}:r{rev}\"; " +
            "при промаху — завантажити повну структуру ОДНИМ набором запитів (аркуші, таблиці, " +
            "колонки, рядки, формули, стилі), побудувати індекси ColumnsById і RowsByKey, " +
            "покласти в кеш без абсолютного терміну. " +
            "Не використовувати IDistributedCache: знімок великий, а серіалізація дорожча за перечитування.");

    /// <inheritdoc />
    public Task InvalidateAsync(int templateVersionId, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: прибрати записи з обома ревізіями. Потрібно лише після Publish і міграції — " +
            "у звичайній роботі не викликається.");
}
