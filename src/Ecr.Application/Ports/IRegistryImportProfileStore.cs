// src/Ecr.Application/Ports/IRegistryImportProfileStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>Stored import profiles (<c>cfg.RegistryImportProfile</c>) of a registry (NAE plan N3).</summary>
public interface IRegistryImportProfileStore
{
    /// <summary>Profiles of a registry, by code.</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<RegistryImportProfile>> ListAsync(int registryDefId, CancellationToken ct);

    /// <summary>A profile by code (tracked); <c>null</c> when absent.</summary>
    /// <param name="registryDefId">Registry.</param>
    /// <param name="code">Profile code.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<RegistryImportProfile?> FindAsync(int registryDefId, string code, CancellationToken ct);

    /// <summary>Adds a new profile.</summary>
    /// <param name="profile">Profile.</param>
    public void Add(RegistryImportProfile profile);

    /// <summary>Makes the next save compare against the token the caller read (optimistic concurrency).</summary>
    /// <param name="profile">Tracked profile.</param>
    /// <param name="rowVersion">Token read with the profile.</param>
    public void Expect(RegistryImportProfile profile, byte[] rowVersion);
}