// src/Ecr.Infrastructure/Persistence/RegistryUseStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryUseStore"/> над <see cref="EcrDbContext"/> (RT-23b).</summary>
/// <remarks>
/// ⚠ Не зберігає сама: видалення старих ребер версії й вставка нових лягають у той самий
/// <c>SaveChanges</c> <c>IUnitOfWork</c>, що й публікація методології, — проміжного стану
/// «ребер немає» ніхто не бачить, порядок блокувань публікації не змінено.
/// </remarks>
public sealed class RegistryUseStore(EcrDbContext db) : IRegistryUseStore
{
    /// <inheritdoc />
    public async Task ReplaceMethodologyUsesAsync(
        int methodologyVersionId, IReadOnlyCollection<RegistryUse> uses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uses);

        // ⚠ Лише ребра ЦІЄЇ версії методології (SourceKind = 1): ребра шаблонів (0), правил
        // довідників (2) і інших версій — не чіпаємо, навіть якщо SourceId збігся числом.
        var stale = await db.RegistryUses
            .Where(u => u.SourceKind == RegistryUse.MethodologyVersionSource && u.SourceId == methodologyVersionId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        db.RegistryUses.RemoveRange(stale);
        db.RegistryUses.AddRange(uses);
    }
}