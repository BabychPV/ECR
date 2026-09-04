// src/Ecr.Application/Ports/IMetadataCache.cs

using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Кеш метаданих шаблону. Опублікована версія структурно незмінна, тому ключ
/// <c>v{id}:r{rev}</c> робить інвалідацію непотрібною: презентаційна правка
/// створює новий ключ, а не псує старий (D-16). Це прибирає когерентність кешу
/// між інстансами як клас проблеми.
/// </summary>
public interface IMetadataCache
{
    /// <summary>Повна структура версії шаблону.</summary>
    public Task<TemplateVersionSnapshot> GetAsync(int templateVersionId, CancellationToken ct);

    /// <summary>Скидає запис. Потрібно лише після <c>Publish</c> або міграції.</summary>
    public Task InvalidateAsync(int templateVersionId, CancellationToken ct);
}
