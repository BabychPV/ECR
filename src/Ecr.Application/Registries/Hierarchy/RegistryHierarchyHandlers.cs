// src/Ecr.Application/Registries/Hierarchy/RegistryHierarchyHandlers.cs
namespace Ecr.Application.Registries.Hierarchy;

// Contract-first skeleton (NAE plan N1): signatures are final, bodies land in the next commits of lane/integ-nae-n1.

/// <summary>Reads hierarchy settings of a registry. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
public sealed class GetRegistryHierarchyHandler
{
    /// <summary>Hierarchy settings.</summary>
    public Task<RegistryHierarchyDto> HandleAsync(string registryCode, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>Saves hierarchy settings. Permission <c>Registry.EditDefinition</c> or grant <c>Write</c>.</summary>
public sealed class SaveRegistryHierarchyHandler
{
    /// <summary>Replaces the level list.</summary>
    public Task<RegistryHierarchyDto> HandleAsync(
        string registryCode, SaveRegistryHierarchyRequest request, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>Reads the tree. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
public sealed class GetRegistryTreeHandler
{
    /// <summary>Flat node list with levels.</summary>
    public Task<RegistryTreeDto> HandleAsync(
        string registryCode, DateOnly? asOf, bool includeRetired, string? query, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>Moves an entry. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
public sealed class MoveRegistryEntryHandler
{
    /// <summary>Re-parents, re-levels and re-orders an entry.</summary>
    public Task<MoveRegistryEntryResponse> HandleAsync(
        string registryCode, long entryId, MoveRegistryEntryRequest request, CancellationToken ct)
        => throw new NotImplementedException();
}

/// <summary>Retires a node softly. Permission <c>Registry.EditData</c> or grant <c>Write</c>.</summary>
public sealed class RetireRegistryEntryHandler
{
    /// <summary>Closes the validity window of the node (and its subtree with <c>Cascade</c>).</summary>
    public Task<RetireRegistryEntryResponse> HandleAsync(
        string registryCode, long entryId, RetireRegistryEntryRequest request, CancellationToken ct)
        => throw new NotImplementedException();
}
