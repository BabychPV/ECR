// src/Ecr.Application/Permits/AirLimits/AirScopeColumns.cs
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>A scope column of the AIR sources table as configured on the type.</summary>
/// <param name="ColumnCode"><c>REGION</c> or <c>AREA</c>.</param>
/// <param name="Kind"><see cref="AirLimitsSchema.KindText"/>, <see cref="AirLimitsSchema.KindRegistry"/> or <see cref="AirLimitsSchema.KindList"/>.</param>
/// <param name="RegistryCode">Registry the values come from (Registry / List); <c>null</c> for Text.</param>
/// <param name="Required">The column is required on the type.</param>
public sealed record AirScopeColumn(string ColumnCode, string Kind, string? RegistryCode, bool Required = false)
{
    /// <summary>The value is a registry entry (Registry or List): the row stores its id.</summary>
    public bool IsReference => Kind != AirLimitsSchema.KindText;
}

/// <summary>
/// ✎ AIR-ON-AIR (human 02.11: kind configurable «тест чи дикшиорнари чи через крапку з комою»): reads how REGION and AREA are
/// configured on the PUBLISHED AIR type with the ordinary column settings — <c>String</c> → Text; <c>Lookup</c> +
/// <c>SharedRegistry</c> → Registry; <c>Lookup</c> + <c>TypeOnly</c> (values entered with «;») → List. No AIR type or no sources table
/// yet → the defaults: REGION = Registry <see cref="AirLimitsSettings.RegionRegistryCode"/>, AREA = Registry
/// <see cref="AirLimitsSettings.AreaRegistryCode"/>.
/// </summary>
public sealed class AirScopeColumns(AirLimitsSettings settings, IPermitTypeStore permits, IRegistryStore registries)
{
    /// <summary>Both scope columns.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(AirScopeColumn Region, AirScopeColumn Area)> ReadAsync(CancellationToken ct)
    {
        var fallback = (
            new AirScopeColumn(AirLimitsSchema.Region, AirLimitsSchema.KindRegistry, settings.RegionRegistryCode, Required: true),
            new AirScopeColumn(AirLimitsSchema.Area, AirLimitsSchema.KindRegistry, settings.AreaRegistryCode, Required: false));

        var type = await permits.FindByCodeAsync(AirLimitsSchema.TypeCode, ct).ConfigureAwait(false);
        if (type is null || type.Status != PermitTypeStatus.Published)
        {
            return fallback;
        }

        var content = await new PermitTypeReader(registries, permits).ReadPublishedAsync(type, ct).ConfigureAwait(false);
        var table = content.Tables.FirstOrDefault(t => string.Equals(t.Code, AirLimitsSchema.SourcesTable, StringComparison.OrdinalIgnoreCase));
        if (table is null)
        {
            return fallback;
        }

        return (Of(table, AirLimitsSchema.Region) ?? fallback.Item1, Of(table, AirLimitsSchema.Area) ?? fallback.Item2);
    }

    /// <summary>Kind of one configured column; <c>null</c> when the table has no such column.</summary>
    public static AirScopeColumn? Of(PermitTypeTableDto table, string columnCode)
    {
        ArgumentNullException.ThrowIfNull(table);
        var column = table.Columns.FirstOrDefault(c => string.Equals(c.Code, columnCode, StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            return null;
        }

        if (!string.Equals(column.DataType, nameof(CellDataType.Lookup), StringComparison.Ordinal))
        {
            return new AirScopeColumn(columnCode, AirLimitsSchema.KindText, null, column.Required);
        }

        return column.ListScope == nameof(PermitListScope.TypeOnly)
            ? new AirScopeColumn(
                columnCode, AirLimitsSchema.KindList,
                PermitHeaderFields.ListRegistryCode(AirLimitsSchema.TypeCode, table.Code, column.Code), column.Required)
            : new AirScopeColumn(columnCode, AirLimitsSchema.KindRegistry, column.ListRegistryCode, column.Required);
    }

    /// <summary>English name of an entry, else its code.</summary>
    public static string NameOf(RegistryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.DisplayL10n.Get("en") ?? entry.Code;
    }
}
