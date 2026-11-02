// src/Ecr.Application/Permits/AirLimits/GetAirScopeFieldsHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// ✎ AIR-ON-AIR: how the import scope (REGION, AREA) is configured on the AIR permit type and which values it offers, each mapped to
/// the NAE tree through <see cref="AirScopeMatcher"/>. Permission <c>Registry.View</c>.
/// </summary>
/// <remarks>
/// A registry that does not exist is NOT created here (Land bootstrap owns <c>LAND_REGION</c>; provisioning owns <c>AIR_AREA</c>):
/// <c>registryFound = false</c> and no options — the wizard shows an empty state.
/// </remarks>
public sealed class GetAirScopeFieldsHandler(
    AirScopeColumns columns,
    IRegistryStore registries,
    GetAirScopeCodesHandler naeScopes,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Both scope columns with their values.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirScopeFieldsDto> HandleAsync(CancellationToken ct)
    {
        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        IReadOnlyList<AirRegionCodeDto> tree;
        try
        {
            tree = (await naeScopes.ReadAsync(ct).ConfigureAwait(false)).Regions;
        }
        catch (Errors.BusinessRuleException)
        {
            // Not provisioned yet (or no NAE tree): the values are still listed, just without a NAE mapping.
            tree = [];
        }
        var (region, area) = await columns.ReadAsync(ct).ConfigureAwait(false);

        // REGION is always required for an import (it scopes the replace); AREA as configured on the type.
        var regionField = await FieldAsync(region, required: true, tree, ct).ConfigureAwait(false);
        var areaField = await FieldAsync(area, area.Required, tree, ct).ConfigureAwait(false);
        return new AirScopeFieldsDto(AirLimitsSchema.TypeCode, regionField, areaField);
    }

    private async Task<AirScopeFieldDto> FieldAsync(
        AirScopeColumn column, bool required, IReadOnlyList<AirRegionCodeDto> tree, CancellationToken ct)
    {
        if (column.Kind == AirLimitsSchema.KindText || column.RegistryCode is null)
        {
            return new AirScopeFieldDto(column.ColumnCode, column.Kind, null, required, RegistryFound: true, []);
        }

        var def = await registries.FindDefinitionAsync(column.RegistryCode, ct).ConfigureAwait(false);
        if (def is null)
        {
            return new AirScopeFieldDto(column.ColumnCode, column.Kind, column.RegistryCode, required, RegistryFound: false, []);
        }

        var entries = (await registries.ListEntriesAsync(def.Id, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted)
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .Select(e =>
            {
                var name = AirScopeColumns.NameOf(e);
                var isRegion = column.ColumnCode == AirLimitsSchema.Region;
                var region = isRegion ? AirScopeMatcher.Match(name, null, tree) : null;
                return new AirScopeEntryDto(
                    column.Kind == AirLimitsSchema.KindList ? null : e.Id,
                    column.Kind == AirLimitsSchema.KindList ? name : e.Code,
                    name,
                    region?.NaeRegion,
                    isRegion ? null : AirScopeMatcher.AreaCode(name, tree));
            })
            .ToList();

        return new AirScopeFieldDto(column.ColumnCode, column.Kind, column.RegistryCode, required, RegistryFound: true, entries);
    }
}
