using Ecr.Application.Ports;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Прогріває кеш метаданих активних версій. Без цього перші користувачі після
/// деплою платять за завантаження схеми.
/// </summary>
public sealed class MetadataWarmup(EcrDbContext db, IMetadataCache cache)
{
    /// <summary>Завантажує знімки всіх версій, на яких є активні проєкти.</summary>
    public Task WarmupAsync(CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: знайти TemplateVersionId активних проєктів; для кожного викликати cache.GetAsync. " +
            "Помилка прогріву не має валити старт — це Warning, не Critical.");
}
