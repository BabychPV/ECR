// src/Ecr.Application/Registries/Nae/NaeTreeManualHandler.cs
namespace Ecr.Application.Registries.Nae;

/// <summary>NAE tree without AF: manual roots, seed roots from <c>LAND_REGION</c>, Excel import (contract; body follows).</summary>
public sealed class NaeTreeManualHandler
{
    /// <summary>Permission to write the tree.</summary>
    public const string EditPermission = "Registry.EditData";

    /// <summary>Permission to download the template.</summary>
    public const string ViewPermission = "Registry.View";

    /// <summary>Creates a region root (and an optional area).</summary>
    public Task<NaeRootResultDto> CreateRootAsync(CreateNaeRootRequest request, CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");

    /// <summary>Region roots from <c>LAND_REGION</c> for an empty tree.</summary>
    public Task<NaeSeedRootsResultDto> SeedRootsAsync(CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");

    /// <summary>Empty template.</summary>
    public Task<byte[]> TemplateAsync(CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");

    /// <summary>File-only analysis.</summary>
    public Task<NaeTreeImportReportDto> AnalyseAsync(Stream file, CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");

    /// <summary>Preview over the stored tree.</summary>
    public Task<NaeTreeImportReportDto> PreviewAsync(Stream file, NaeTreeImportOptions options, CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");

    /// <summary>All-or-nothing apply.</summary>
    public Task<NaeTreeImportReportDto> ApplyAsync(Stream file, NaeTreeImportOptions options, CancellationToken ct)
        => throw new NotSupportedException("Contract commit: the implementation follows in the same lane.");
}
