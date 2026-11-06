// src/Ecr.Application/Permits/Import/PermitImportTables.cs
using System.Text;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Import;

/// <summary>What the paste/import of a permit table needs to know about the table and the NAE/Region/Area registries.</summary>
/// <param name="Table">Table registry.</param>
/// <param name="NaeDefId">NAE tree registry; <c>null</c> — absent.</param>
/// <param name="RegionDefId">Region registry (<see cref="AirLimitsSettings.RegionRegistryCode"/>); <c>null</c> — absent.</param>
/// <param name="AreaDefId">Area registry (<see cref="AirLimitsSettings.AreaRegistryCode"/>); <c>null</c> — absent.</param>
internal sealed record PermitImportTable(RegistryDef Table, int? NaeDefId, int? RegionDefId, int? AreaDefId)
{
    /// <summary>Lookup columns of the table.</summary>
    public IEnumerable<RegistryFieldDef> Lookups
        => Table.Fields.Where(f => f.DataType == CellDataType.Lookup && f.RefRegistryDefId is not null
                                   && f.Code != PermitHeaderFields.PermitFieldCode);

    /// <summary>Columns that reference the NAE tree.</summary>
    public IReadOnlyList<RegistryFieldDef> NaeColumns
        => NaeDefId is { } nae ? [.. Lookups.Where(f => f.RefRegistryDefId == nae)] : [];

    /// <summary>Region columns (derivable only when the table has a NAE column).</summary>
    public IReadOnlyList<RegistryFieldDef> RegionColumns
        => NaeColumns.Count > 0 && RegionDefId is { } region ? [.. Lookups.Where(f => f.RefRegistryDefId == region)] : [];

    /// <summary>Area columns (derivable only when the table has a NAE column).</summary>
    public IReadOnlyList<RegistryFieldDef> AreaColumns
        => NaeColumns.Count > 0 && AreaDefId is { } area ? [.. Lookups.Where(f => f.RefRegistryDefId == area)] : [];
}

/// <summary>Shared helpers of the permit table paste/import endpoints.</summary>
internal static class PermitImportTables
{
    /// <summary>Profile code of the saved paste mapping in <c>cfg.RegistryImportProfile</c> of the table registry.</summary>
    public const string ProfileCode = "PASTE_DEFAULT";

    /// <summary>
    /// The registry is a permit TABLE (<c>PRM_…</c> with the composition field <c>PERMIT</c>); any other registry is answered as
    /// absent (<c>404 err.ECR-REG-0404.registry</c>), like a registry hidden by a deny.
    /// </summary>
    public static RegistryDef RequireTable(RegistryDef definition, string code)
        => IsPermitTable(definition) ? definition : throw RegistryAccess.NotFound(code);

    /// <summary>A permit table registry: <c>PRM_…</c> with the composition field <c>PERMIT</c>.</summary>
    public static bool IsPermitTable(RegistryDef definition)
        => definition.Code.StartsWith("PRM_", StringComparison.Ordinal)
           && definition.Fields.Any(f => f.Code == PermitHeaderFields.PermitFieldCode && f.RelationKind == RegistryRelationKind.Composition);

    /// <summary>The table with the ids of the NAE / Region / Area registries.</summary>
    public static async Task<PermitImportTable> DescribeAsync(
        IRegistryStore registries, RegistryDef table, NaeTreeSettings? nae, AirLimitsSettings? air, CancellationToken ct)
    {
        var naeDef = await registries.FindDefinitionAsync((nae ?? NaeTreeSettings.Default).RegistryCode, ct).ConfigureAwait(false);
        var settings = air ?? AirLimitsSettings.Default;
        var region = await registries.FindDefinitionAsync(settings.RegionRegistryCode, ct).ConfigureAwait(false);
        var area = await registries.FindDefinitionAsync(settings.AreaRegistryCode, ct).ConfigureAwait(false);
        return new PermitImportTable(table, naeDef?.Id, region?.Id, area?.Id);
    }

    /// <summary>
    /// File header as a mapping key: compatibility-normalised (NFKC), lower case, letters and digits only. The client
    /// (<c>importMapping.ts</c>, <c>normalizeHeader</c>) uses the same rule.
    /// </summary>
    public static string NormalizeHeader(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(header.Length);
        foreach (var c in header.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
