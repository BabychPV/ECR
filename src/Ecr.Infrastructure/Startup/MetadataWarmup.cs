using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Startup;

/// <summary>
/// Прогріває кеш метаданих активних версій. Без цього перші користувачі після
/// деплою платять за завантаження схеми.
/// </summary>
public sealed class MetadataWarmup(EcrDbContext db, IMetadataCache cache)
{
    /// <summary>Скільки версій прогрівати щонайбільше.</summary>
    /// <remarks>
    /// Активних проєктів десятки, версій — менше. Межа не дає прогріву
    /// перетворити старт застосунку на кількахвилинне очікування, якщо
    /// проєктів раптом виявиться тисяча.
    /// </remarks>
    private const int MaxVersions = 200;

    /// <summary>Завантажує знімки всіх версій, на яких є активні проєкти.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки версій прогріто.</returns>
    /// <remarks>
    /// ⚠ Помилка прогріву **не валить старт**. Прогрів — оптимізація: без
    /// нього перші користувачі платять за завантаження схеми, але система
    /// працює. Застосунок, що не піднявся через непрогрітий кеш, гірший за
    /// повільний перший запит.
    /// </remarks>
    public async Task<int> WarmupAsync(CancellationToken ct)
    {
        var versions = await db.Projects
            .AsNoTracking()
            .Where(p => p.Status != Domain.Enums.ProjectStatus.Closed)
            .Select(p => p.TemplateVersionId)
            .Distinct()
            .Take(MaxVersions)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var warmed = 0;

        foreach (var versionId in versions)
        {
            try
            {
                await cache.GetAsync(versionId, ct).ConfigureAwait(false);
                warmed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Одна зіпсована версія не має зупиняти прогрів решти: вона
                // однаково впаде на першому запиті до неї, і там причина буде
                // видніша.
            }
        }

        return warmed;
    }
}
