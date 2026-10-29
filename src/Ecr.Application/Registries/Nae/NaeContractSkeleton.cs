// src/Ecr.Application/Registries/Nae/NaeContractSkeleton.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>Contract skeleton (NAE plan N2): replaced by the real handler in the next commit of the lane.</summary>
public sealed class ImportNaeFromAfHandler
{
    /// <summary>Loads the NAE tree from AF.</summary>
    /// <param name="request">Request.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<NaeAfImportReport> HandleAsync(ImportNaeFromAfRequest request, CancellationToken ct)
        => throw new NotImplementedException("NAE N2: contract skeleton");
}

/// <summary>Contract skeleton (NAE plan N2): replaced by the real handler in the next commit of the lane.</summary>
public sealed class GetRegistryEntryReferencesHandler
{
    /// <summary>Where an entry is used.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<RegistryEntryReferencesDto> HandleAsync(string registryCode, long entryId, CancellationToken ct)
        => throw new NotImplementedException("NAE N2: contract skeleton");
}
