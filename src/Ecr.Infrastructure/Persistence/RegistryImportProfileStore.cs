using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary><see cref="IRegistryImportProfileStore"/> over <see cref="EcrDbContext"/>.</summary>
public sealed class RegistryImportProfileStore(EcrDbContext db) : IRegistryImportProfileStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryImportProfile>> ListAsync(int registryDefId, CancellationToken ct)
        => await db.RegistryImportProfiles.AsNoTracking()
            .Where(p => p.RegistryDefId == registryDefId).OrderBy(p => p.Code).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RegistryImportProfile?> FindAsync(int registryDefId, string code, CancellationToken ct)
        => db.RegistryImportProfiles.FirstOrDefaultAsync(p => p.RegistryDefId == registryDefId && p.Code == code, ct);

    /// <inheritdoc />
    public void Add(RegistryImportProfile profile) => db.RegistryImportProfiles.Add(profile);

    /// <inheritdoc />
    public void Expect(RegistryImportProfile profile, byte[] rowVersion)
        => db.Entry(profile).Property(p => p.RowVersion).OriginalValue = rowVersion;
}