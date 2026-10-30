// src/Ecr.Application/Permits/AirLimits/AirLimitsImportHandler.cs
namespace Ecr.Application.Permits.AirLimits;

/// <summary>Wizard backend: analyse, preview and apply of an Excel with emission parameters. SKELETON of the contract commit.</summary>
public sealed class AirLimitsImportHandler
{
    /// <summary>Permission to analyse, preview and apply an import.</summary>
    public const string Permission = "Registry.EditData";

    /// <summary>Layout of the file (header, mapping, counts, findings). Permission <c>Registry.EditData</c>.</summary>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile or specification.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<AirLimitsAnalysisDto> AnalyseAsync(Stream file, AirLimitsImportOptions options, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");

    /// <summary>Preview: nothing is written. Permission <c>Registry.EditData</c> on the permit's tables.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile, region, area, decisions.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<AirLimitsPreviewDto> PreviewAsync(long permitId, Stream file, AirLimitsImportOptions options, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");

    /// <summary>Apply: the same plan as the preview, all or nothing, one transaction.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="file">The workbook.</param>
    /// <param name="options">Profile, region, area, decisions, confirmed owners, expected fingerprint.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<AirLimitsImportResultDto> ApplyAsync(long permitId, Stream file, AirLimitsImportOptions options, CancellationToken ct)
        => throw new NotImplementedException("Contract commit.");
}