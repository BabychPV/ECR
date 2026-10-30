// src/Ecr.Application/Permits/AirLimits/AirLimitsData.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>A registry row read with its values by field code.</summary>
/// <param name="Entry">The entry.</param>
/// <param name="Values">Values by field code.</param>
internal sealed record AirRow(RegistryEntry Entry, IReadOnlyDictionary<string, RegistryRowValue> Values)
{
    /// <summary>Text value of a field.</summary>
    public string? Text(string code) => Values.TryGetValue(code, out var v) ? v.Text : null;

    /// <summary>Number of a field.</summary>
    public decimal? Number(string code) => Values.TryGetValue(code, out var v) ? v.Numeric : null;

    /// <summary>Lookup target of a field.</summary>
    public long? Ref(string code) => Values.TryGetValue(code, out var v) ? v.RefEntryId : null;
}

/// <summary>The registries of the air-limits permit, resolved once per request.</summary>
internal sealed class AirTables
{
    /// <summary>Sources table.</summary>
    public required RegistryDef Sources { get; init; }

    /// <summary>Substances table.</summary>
    public required RegistryDef Substances { get; init; }

    /// <summary>Permit header.</summary>
    public required RegistryDef Header { get; init; }

    /// <summary>NAE tree.</summary>
    public required RegistryDef Nae { get; init; }

    /// <summary>Substance registry; <c>null</c> when it does not exist.</summary>
    public RegistryDef? SubstanceRegistry { get; init; }

    /// <summary>Id of a field by code.</summary>
    public static int Field(RegistryDef def, string code)
        => def.Fields.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase))?.Id
           ?? throw new InvalidOperationException($"Registry {def.Code} has no field {code}.");
}

/// <summary>Reads the air-limits registries through the existing registry ports (no SQL of its own).</summary>
internal sealed class AirLimitsData(IRegistryStore registries, IRegistryRowsQuery rows, AirLimitsSettings settings)
{
    private const int Chunk = 400;

    // One import reads the same big lists many times; they are cached per request and dropped after every write (Invalidate).
    private readonly Dictionary<int, IReadOnlyList<RegistryEntry>> _entries = [];
    private readonly Dictionary<int, IReadOnlyList<RegistryRowValue>> _fieldValues = [];

    /// <summary>Drops the cached lists: call after any write.</summary>
    public void Invalidate()
    {
        _entries.Clear();
        _fieldValues.Clear();
    }

    private async Task<IReadOnlyList<RegistryEntry>> EntriesAsync(RegistryDef def, CancellationToken ct)
    {
        if (!_entries.TryGetValue(def.Id, out var list))
        {
            list = await rows.ListEntriesAsync(def.Id, null, ct).ConfigureAwait(false);
            _entries[def.Id] = list;
        }

        return list;
    }

    private async Task<IReadOnlyList<RegistryRowValue>> FieldValuesAsync(int fieldId, CancellationToken ct)
    {
        if (!_fieldValues.TryGetValue(fieldId, out var list))
        {
            list = await rows.ListFieldValuesAsync([fieldId], null, ct).ConfigureAwait(false);
            _fieldValues[fieldId] = list;
        }

        return list;
    }

    /// <summary>Registries of the type; <c>422 airNotProvisioned</c> when the type is not published yet.</summary>
    public async Task<AirTables> TablesAsync(CancellationToken ct)
    {
        var sources = await registries.FindDefinitionAsync(AirLimitsSchema.SourcesRegistry, ct).ConfigureAwait(false);
        var substances = await registries.FindDefinitionAsync(AirLimitsSchema.SubstancesRegistry, ct).ConfigureAwait(false);
        var header = await registries.FindDefinitionAsync(AirLimitsSchema.HeaderRegistry, ct).ConfigureAwait(false);
        var nae = await registries.FindDefinitionAsync(settings.NaeRegistryCode, ct).ConfigureAwait(false);

        if (sources is null || substances is null || header is null || nae is null
            || !sources.Fields.Any(f => f.Code == AirLimitsSchema.Nae))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "The Air Limits Import permit type is not provisioned (or the NAE tree does not exist).",
                new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.NotProvisioned });
        }

        return new AirTables
        {
            Sources = sources,
            Substances = substances,
            Header = header,
            Nae = nae,
            SubstanceRegistry = await registries.FindDefinitionAsync(settings.SubstanceRegistryCode, ct).ConfigureAwait(false),
        };
    }

    /// <summary>Ids of the live entries of a table whose lookup field points at <paramref name="targets"/> (any of them).</summary>
    public async Task<HashSet<long>> EntriesPointingAtAsync(
        RegistryDef def, string fieldCode, IReadOnlyCollection<long> targets, CancellationToken ct)
    {
        if (targets.Count == 0)
        {
            return [];
        }

        var wanted = targets as ISet<long> ?? targets.ToHashSet();
        var values = await FieldValuesAsync(AirTables.Field(def, fieldCode), ct).ConfigureAwait(false);
        var hit = values.Where(v => v.RefEntryId is { } r && wanted.Contains(r)).Select(v => v.RegistryEntryId).ToHashSet();
        return await LiveAsync(def, hit, ct).ConfigureAwait(false);
    }

    /// <summary>Entries of <paramref name="ids"/> that are not deleted.</summary>
    public async Task<HashSet<long>> LiveAsync(RegistryDef def, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var live = (await EntriesAsync(def, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).Select(e => e.Id).ToHashSet();
        return [.. ids.Where(live.Contains)];
    }

    /// <summary>Reads entries with all their values.</summary>
    public async Task<Dictionary<long, AirRow>> ReadAsync(RegistryDef def, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, AirRow>();
        if (ids.Count == 0)
        {
            return result;
        }

        var idSet = ids as ISet<long> ?? ids.ToHashSet();
        var entries = (await EntriesAsync(def, ct).ConfigureAwait(false)).Where(e => idSet.Contains(e.Id)).ToDictionary(e => e.Id);
        var codeById = def.Fields.ToDictionary(f => f.Id, f => f.Code);

        foreach (var chunk in ids.Where(entries.ContainsKey).Chunk(Chunk))
        {
            var slice = await rows.ReadRowsAsync(chunk, null, ct).ConfigureAwait(false);
            foreach (var group in slice.Values.GroupBy(v => v.RegistryEntryId))
            {
                var values = group.Where(v => codeById.ContainsKey(v.RegistryFieldDefId))
                    .ToDictionary(v => codeById[v.RegistryFieldDefId], v => v, StringComparer.OrdinalIgnoreCase);
                result[group.Key] = new AirRow(entries[group.Key], values);
            }

            foreach (var id in chunk.Where(c => !result.ContainsKey(c) && entries.ContainsKey(c)))
            {
                result[id] = new AirRow(entries[id], new Dictionary<string, RegistryRowValue>());
            }
        }

        return result;
    }

    /// <summary>
    /// NAE tree nodes of the given codes inside a region and area: normalised code → entry id (the first live one).
    /// </summary>
    public async Task<Dictionary<string, long>> NaeByCodeAsync(
        AirTables tables, IReadOnlyCollection<string> codes, string region, string area, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0)
        {
            return result;
        }

        var wanted = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var codeField = AirTables.Field(tables.Nae, NaeTreeSchema.NaeCodeField);
        var codeValues = (await FieldValuesAsync(codeField, ct).ConfigureAwait(false))
            .Where(v => v.Text is not null && wanted.Contains(NaeCodeNormalizer.Normalize(v.Text) ?? v.Text))
            .ToList();
        var live = await LiveAsync(tables.Nae, codeValues.Select(v => v.RegistryEntryId).ToList(), ct).ConfigureAwait(false);

        var scoped = await ReadAsync(tables.Nae, live, ct).ConfigureAwait(false);
        foreach (var row in scoped.Values.OrderBy(r => r.Entry.Id))
        {
            var rowRegion = AirLimitsSchema.NormalizeScope(row.Text(NaeTreeSchema.RegionField), "");
            var rowArea = AirLimitsSchema.NormalizeScope(row.Text(NaeTreeSchema.AreaField), AirLimitsSchema.NoArea);
            if (rowRegion != region || rowArea != area)
            {
                continue;
            }

            var code = NaeCodeNormalizer.Normalize(row.Text(NaeTreeSchema.NaeCodeField));
            if (code is not null)
            {
                result.TryAdd(code, row.Entry.Id);
            }
        }

        return result;
    }

    /// <summary>Substances of the registry: normalised code → entry id (the first live one).</summary>
    public async Task<Dictionary<string, long>> SubstancesByCodeAsync(
        AirTables tables, IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (tables.SubstanceRegistry is not { } registry || codes.Count == 0)
        {
            return result;
        }

        var field = AirTables.Field(registry, AirLimitsSchema.SubstanceCodeField);
        var wanted = codes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = (await FieldValuesAsync(field, ct).ConfigureAwait(false))
            .Select(v => (v.RegistryEntryId, Code: NaeCodeNormalizer.Normalize(v.Text ?? v.Numeric?.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Where(v => v.Code is not null && wanted.Contains(v.Code))
            .ToList();
        var live = await LiveAsync(registry, values.Select(v => v.RegistryEntryId).ToList(), ct).ConfigureAwait(false);

        foreach (var (id, code) in values.OrderBy(v => v.RegistryEntryId).Where(v => live.Contains(v.RegistryEntryId)))
        {
            result.TryAdd(code!, id);
        }

        return result;
    }

    /// <summary>Normalised code of a substance entry.</summary>
    public async Task<Dictionary<long, string>> SubstanceCodesAsync(
        AirTables tables, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (tables.SubstanceRegistry is not { } registry || ids.Count == 0)
        {
            return result;
        }

        var field = AirTables.Field(registry, AirLimitsSchema.SubstanceCodeField);
        var idSet = ids as ISet<long> ?? ids.ToHashSet();
        foreach (var v in await FieldValuesAsync(field, ct).ConfigureAwait(false))
        {
            if (idSet.Contains(v.RegistryEntryId)
                && NaeCodeNormalizer.Normalize(v.Text ?? v.Numeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } code)
            {
                result[v.RegistryEntryId] = code;
            }
        }

        return result;
    }

    /// <summary>Normalised NAE code of tree nodes.</summary>
    public async Task<Dictionary<long, string>> NaeCodesAsync(AirTables tables, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        if (ids.Count == 0)
        {
            return result;
        }

        var field = AirTables.Field(tables.Nae, NaeTreeSchema.NaeCodeField);
        var idSet = ids as ISet<long> ?? ids.ToHashSet();
        foreach (var v in await FieldValuesAsync(field, ct).ConfigureAwait(false))
        {
            if (idSet.Contains(v.RegistryEntryId) && NaeCodeNormalizer.Normalize(v.Text) is { } code)
            {
                result[v.RegistryEntryId] = code;
            }
        }

        return result;
    }
}

/// <summary>Message keys of the air-limits import (catalog rows in <c>09-seed.sql</c>, section NAE:n3-air).</summary>
public static class AirLimitsKeys
{
    /// <summary>The type is not provisioned.</summary>
    public const string NotProvisioned = "err.ECR-IMP-0422.airNotProvisioned";

    /// <summary>The region is missing.</summary>
    public const string ScopeInvalid = "err.ECR-IMP-0422.airScopeInvalid";

    /// <summary>The permit is not an air-limits permit.</summary>
    public const string NotAirPermit = "err.ECR-IMP-0422.airNotAirPermit";

    /// <summary>The node chosen for a new NAE cannot hold it.</summary>
    public const string CreateUnderInvalid = "err.ECR-IMP-0422.airCreateUnderInvalid";

    /// <summary>The registry writer refused a row.</summary>
    public const string WriteRejected = "err.ECR-IMP-0422.airWriteRejected";

    /// <summary>The profile code is unknown.</summary>
    public const string ProfileUnknown = "err.ECR-IMP-0422.airProfileUnknown";

    /// <summary>The profile changed since it was read.</summary>
    public const string ProfileChanged = "err.ECR-REG-0409.profileChanged";

    /// <summary>The upload is missing.</summary>
    public const string RequestInvalid = "err.ECR-IMP-0422.airRequestInvalid";
}
