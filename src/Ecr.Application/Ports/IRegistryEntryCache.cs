// src/Ecr.Application/Ports/IRegistryEntryCache.cs

using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Ports;

/// <summary>
/// Кеш резолвлених списків довідника. Ключ несе <c>DataRevision</c>, тому
/// інвалідація не потрібна — так само, як із метаданими (D-16).
/// </summary>
/// <remarks>
/// ⚠ Окремий порт, а не <c>IMemoryCache</c> у хендлері: застосунок не повинен
/// залежати від <c>Microsoft.Extensions.Caching</c>, і архітектурний тест це
/// перевіряє. Порт вузький навмисно — кешується рівно одна річ.
/// </remarks>
public interface IRegistryEntryCache
{
    /// <summary>Віддає збережений список або будує і зберігає новий.</summary>
    /// <param name="key">Ключ, що вже містить ревізію даних і дату.</param>
    /// <param name="factory">Побудова списку, якщо в кеші його немає.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<RegistryEntry>> GetOrAddAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<RegistryEntry>>> factory,
        CancellationToken ct);
}
