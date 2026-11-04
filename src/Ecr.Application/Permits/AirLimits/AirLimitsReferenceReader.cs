// src/Ecr.Application/Permits/AirLimits/AirLimitsReferenceReader.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// "Where used" of a registry entry (NAE plan N2/N3): the permits whose source rows point at an NAE node, or whose substance rows
/// point at a substance entry, with permit number, type, validity and the number of substance rows.
/// </summary>
public sealed class AirLimitsReferenceReader(AirLimitsSettingsAccessor accessor) : IRegistryEntryReferenceReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RegistryEntryReferenceDto>> ListAsync(int registryDefId, long entryId, CancellationToken ct)
    {
        var data = accessor.Data;
        AirTables tables;
        try
        {
            tables = await data.TablesAsync(ct).ConfigureAwait(false);
        }
        catch (BusinessRuleException)
        {
            // The type is not provisioned: nothing can point at the entry.
            return [];
        }

        Dictionary<long, AirRow> sources;
        Dictionary<long, AirRow>? substanceRows = null;

        if (registryDefId == tables.Nae.Id)
        {
            var ids = await data.EntriesPointingAtAsync(tables.Sources, AirLimitsSchema.Nae, [entryId], ct).ConfigureAwait(false);
            sources = await data.ReadAsync(tables.Sources, ids, ct).ConfigureAwait(false);
        }
        else if (tables.SubstanceRegistry is { } registry && registryDefId == registry.Id)
        {
            var childIds = await data.EntriesPointingAtAsync(tables.Substances, AirLimitsSchema.Substance, [entryId], ct).ConfigureAwait(false);
            substanceRows = await data.ReadAsync(tables.Substances, childIds, ct).ConfigureAwait(false);
            var sourceIds = substanceRows.Values.Select(r => r.Ref(AirLimitsSchema.Source)).OfType<long>().Distinct().ToList();
            sources = await data.ReadAsync(tables.Sources, await data.LiveAsync(tables.Sources, sourceIds, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        else
        {
            return [];
        }

        var permitField = PermitHeaderFields.PermitFieldCode;
        var permitIds = sources.Values.Select(s => s.Ref(permitField)).OfType<long>().Distinct().ToList();
        var permits = await data.ReadAsync(tables.Header, await data.LiveAsync(tables.Header, permitIds, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

        // Substance rows per source (for an NAE node: all rows of the source; for a substance: the rows that name it).
        Dictionary<long, int> counts;
        if (substanceRows is not null)
        {
            counts = substanceRows.Values
                .Select(r => r.Ref(AirLimitsSchema.Source)).OfType<long>()
                .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        }
        else
        {
            var childIds = await data.EntriesPointingAtAsync(tables.Substances, AirLimitsSchema.Source, [.. sources.Keys], ct).ConfigureAwait(false);
            var children = await data.ReadAsync(tables.Substances, childIds, ct).ConfigureAwait(false);
            counts = children.Values.Select(r => r.Ref(AirLimitsSchema.Source)).OfType<long>()
                .GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        }

        return
        [
            .. sources.Values
                .Where(s => s.Ref(permitField) is { } p && permits.ContainsKey(p))
                .GroupBy(s => s.Ref(permitField)!.Value)
                .Select(g =>
                {
                    var permit = permits[g.Key];
                    return new RegistryEntryReferenceDto(
                        "permit",
                        g.Key,
                        permit.Text("Permit_Number") ?? permit.Entry.Code,
                        AirLimitsSchema.TypeCode,
                        permit.Entry.ValidFrom,
                        permit.Entry.ValidTo,
                        g.Sum(s => counts.GetValueOrDefault(s.Entry.Id)));
                })
                .OrderBy(r => r.PermitNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.PermitId),
        ];
    }
}
