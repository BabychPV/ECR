using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryDraftStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class RegistryDraftStore(EcrDbContext db) : IRegistryDraftStore
{
    /// <inheritdoc />
    public Task<RegistryDefinitionDraft?> FindAsync(int registryDefId, CancellationToken ct)
        => db.RegistryDefinitionDrafts.FirstOrDefaultAsync(d => d.RegistryDefId == registryDefId, ct);

    /// <inheritdoc />
    public void Add(RegistryDefinitionDraft draft) => db.RegistryDefinitionDrafts.Add(draft);

    /// <inheritdoc />
    public void Remove(RegistryDefinitionDraft draft) => db.RegistryDefinitionDrafts.Remove(draft);
}
