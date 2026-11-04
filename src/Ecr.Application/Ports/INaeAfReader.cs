// src/Ecr.Application/Ports/INaeAfReader.cs
using Ecr.Application.Registries.Nae;

namespace Ecr.Application.Ports;

/// <summary>
/// Reads the AF equipment tree for the one-time NAE load (NAE plan N2). Read-only (D-44); tests use a fake.
/// </summary>
public interface INaeAfReader
{
    /// <summary>All elements under <paramref name="rootPath"/> with the NAE attributes.</summary>
    /// <param name="dataSourceId">PI AF data source.</param>
    /// <param name="rootPath">Root element (path or name, as the transport understands it).</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct);
}

/// <summary>Where a registry entry is used: permits and their limits (read side of the "where used" panel).</summary>
public interface IRegistryEntryReferenceReader
{
    /// <summary>Uses of an entry, ordered by permit number then id.</summary>
    /// <param name="registryDefId">Registry of the entry.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<RegistryEntryReferenceDto>> ListAsync(int registryDefId, long entryId, CancellationToken ct);
}
