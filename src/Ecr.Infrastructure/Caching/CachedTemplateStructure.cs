using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Caching;

/// <summary>
/// <see cref="ITemplateStructure"/> над тим самим кешем, що й
/// <see cref="MetadataCache"/>.
/// </summary>
/// <remarks>
/// Нічого не читає з бази навмисно. Читання тут означало б синхронний запит у
/// коді, який викликається з обробки HTTP-запиту, — а це та сама шкода, що й
/// блокувальне очікування, лише непомітніша.
/// </remarks>
public sealed class CachedTemplateStructure(IMemoryCache memory) : ITemplateStructure
{
    /// <inheritdoc />
    public TemplateVersionSnapshot Get(int templateVersionId)
    {
        if (!memory.TryGetValue(MetadataCache.RevisionKey(templateVersionId), out int revision))
        {
            throw new InvalidOperationException(
                $"Знімка версії {templateVersionId} немає в кеші. " +
                "Спершу викличте IMetadataCache.GetAsync — синхронного читання структури тут немає навмисно.");
        }

        if (memory.TryGetValue(MetadataCache.CacheKey(templateVersionId, revision), out TemplateVersionSnapshot? snapshot)
            && snapshot is not null)
        {
            return snapshot;
        }

        throw new InvalidOperationException(
            $"Знімок версії {templateVersionId} ревізії {revision} витіснено з кешу. " +
            "Повторіть IMetadataCache.GetAsync.");
    }
}
