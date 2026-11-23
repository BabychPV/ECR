// src/Ecr.Application/Permits/AirLimits/GetAirLimitsPermitHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>Permit card content: sources with substances and totals per substance.</summary>
public sealed class GetAirLimitsPermitHandler(
    AirLimitsSettingsAccessor accessor,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditReader audit)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Reads the card. Permission <c>Registry.View</c> or grant <c>Read</c> on the permit's registry.</summary>
    /// <param name="permitId">Permit.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsPermitDto> HandleAsync(long permitId, CancellationToken ct)
    {
        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Read, ct).ConfigureAwait(false);
        if (!string.Equals(type.Code, AirLimitsSchema.TypeCode, StringComparison.Ordinal))
        {
            throw PermitErrors.NotFound(permitId);
        }

        var data = accessor.Data;
        var tables = await data.TablesAsync(ct).ConfigureAwait(false);
        var number = (await data.ReadAsync(tables.Header, [permitId], ct).ConfigureAwait(false)).GetValueOrDefault(permitId)?.Text("Permit_Number")
                     ?? entry.Code;

        var sourceIds = await data.EntriesPointingAtAsync(tables.Sources, PermitHeaderFields.PermitFieldCode, [permitId], ct).ConfigureAwait(false);
        var sources = await data.ReadAsync(tables.Sources, sourceIds, ct).ConfigureAwait(false);
        var childIds = await data.EntriesPointingAtAsync(tables.Substances, AirLimitsSchema.Source, [.. sourceIds], ct).ConfigureAwait(false);
        var children = await data.ReadAsync(tables.Substances, childIds, ct).ConfigureAwait(false);

        var naeCodes = await data.NaeCodesAsync(
            tables, [.. sources.Values.Select(s => s.Ref(AirLimitsSchema.Nae)).OfType<long>().Distinct()], ct).ConfigureAwait(false);
        var substanceIds = children.Values.Select(c => c.Ref(AirLimitsSchema.Substance)).OfType<long>().Distinct().ToList();
        var substanceCodes = await data.SubstanceCodesAsync(tables, substanceIds, ct).ConfigureAwait(false);
        var substanceNames = tables.SubstanceRegistry is { } registry
            ? (await rows.ListEntriesAsync(registry.Id, null, ct).ConfigureAwait(false))
                .Where(e => substanceIds.Contains(e.Id)).ToDictionary(e => e.Id, e => e.DisplayL10n.Get("en"))
            : [];

        var childrenBySource = children.Values
            .Where(c => c.Ref(AirLimitsSchema.Source) is not null && c.Ref(AirLimitsSchema.Substance) is { } s && substanceCodes.ContainsKey(s))
            .ToLookup(c => c.Ref(AirLimitsSchema.Source)!.Value);

        // P1 07.11: the columns added to the tables («Add as new column») are shown with their values, and every row carries the
        // version the generic batch write checks (row editing on the card).
        var addedSources = AirLimitsSchema.AddedSourceColumns(tables.Sources);
        var addedSubstances = AirLimitsSchema.AddedSubstanceColumns(tables.Substances);

        static Dictionary<string, object?> Added(AirRow row, IReadOnlyList<Domain.Entities.Configuration.RegistryFieldDef> columns)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                if (row.Value(column) is { } value)
                {
                    values[column.Code] = value;
                }
            }

            return values;
        }

        AirSubstanceDto Substance(AirRow c)
        {
            var sid = c.Ref(AirLimitsSchema.Substance)!.Value;
            return new AirSubstanceDto(
                c.Entry.Id, sid, substanceCodes[sid], substanceNames.GetValueOrDefault(sid),
                c.Number(AirLimitsSchema.GramsPerSecond), c.Number(AirLimitsSchema.MgPerNm3), c.Number(AirLimitsSchema.TonsPerYear),
                Added(c, addedSubstances), c.VersionToken);
        }

        // ✎ AIR-ON-AIR: REGION / AREA hold an entry (Registry / List kind) or a text (Text kind); the answer carries both forms.
        var scopeIds = sources.Values
            .SelectMany(s => new[] { s.Ref(AirLimitsSchema.Region), s.Ref(AirLimitsSchema.Area) })
            .OfType<long>()
            .Distinct()
            .ToList();
        var scopeCodes = new Dictionary<long, string>();
        foreach (var id in scopeIds)
        {
            if (await registries.FindEntryAsync(id, ct).ConfigureAwait(false) is { } scopeEntry)
            {
                scopeCodes[id] = scopeEntry.Code;
            }
        }

        string? ScopeText(AirRow s, string column)
            => s.Ref(column) is { } id ? scopeCodes.GetValueOrDefault(id) : s.Text(column);

        var list = new List<AirSourceDto>();
        foreach (var s in sources.Values.Where(s => s.Ref(AirLimitsSchema.Nae) is { } n && naeCodes.ContainsKey(n)))
        {
            var nae = s.Ref(AirLimitsSchema.Nae)!.Value;
            var regionId = s.Ref(AirLimitsSchema.Region);
            var areaId = s.Ref(AirLimitsSchema.Area);
            list.Add(new AirSourceDto(
                s.Entry.Id, nae, naeCodes[nae], ScopeText(s, AirLimitsSchema.Region), ScopeText(s, AirLimitsSchema.Area),
                s.Text(AirLimitsSchema.SiteLabel), s.Text(AirLimitsSchema.ProductionCode), s.Text(AirLimitsSchema.SourceName),
                s.Number(AirLimitsSchema.Quantity), s.Number(AirLimitsSchema.HoursPerYear), s.Text(AirLimitsSchema.PointName),
                s.Text(AirLimitsSchema.SourceType), s.Number(AirLimitsSchema.Height), s.Number(AirLimitsSchema.Diameter),
                s.Number(AirLimitsSchema.Velocity), s.Number(AirLimitsSchema.Volume), s.Number(AirLimitsSchema.Temperature),
                s.Number(AirLimitsSchema.X1), s.Number(AirLimitsSchema.Y1), s.Number(AirLimitsSchema.X2), s.Number(AirLimitsSchema.Y2),
                [.. childrenBySource[s.Entry.Id].Select(Substance).OrderBy(x => x.SubstanceCode, StringComparer.Ordinal)],
                regionId,
                regionId is { } r ? scopeCodes.GetValueOrDefault(r) : null,
                areaId,
                areaId is { } a ? scopeCodes.GetValueOrDefault(a) : null,
                Added(s, addedSources),
                s.VersionToken));
        }

        list = [.. list.OrderBy(s => s.Region ?? string.Empty, StringComparer.Ordinal).ThenBy(s => s.Area ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.NaeCode, StringComparer.Ordinal)];

        var totals = list.SelectMany(s => s.Substances)
            .GroupBy(x => x.SubstanceEntryId)
            .Select(g => new AirTotalDto(
                g.Key, g.First().SubstanceCode, g.First().SubstanceName, g.Count(),
                g.Sum(x => x.GramsPerSecond ?? 0m), g.Sum(x => x.TonsPerYear ?? 0m)))
            .OrderBy(t => t.SubstanceCode, StringComparer.Ordinal)
            .ToList();

        AirAddedColumnDto Column(string table, Domain.Entities.Configuration.RegistryFieldDef f)
            => new(table, f.Code, new Dictionary<string, string>(f.NameL10n.Values, StringComparer.Ordinal), f.DataType.ToString());

        return new AirLimitsPermitDto(
            permitId, number, type.Code, entry.ValidFrom, entry.ValidTo, list, totals, list.Sum(s => s.Substances.Count),
            [.. addedSources.Select(f => Column("sources", f)), .. addedSubstances.Select(f => Column("substances", f))],
            tables.Sources.Code,
            tables.Substances.Code,
            await CurrentTableAsync(permitId, ct).ConfigureAwait(false) ?? await EarlierTableAsync(tables.Sources.Id, permitId, ct).ConfigureAwait(false));
    }

    /// <summary>Recent imports scanned for a permit imported before D-569 (no per-permit record yet).</summary>
    private const int EarlierImportsScanned = 500;

    /// <summary>
    /// ✎ D-569: a permit imported before the per-permit record existed still names its current table — from the import's record on
    /// the sources table (a seek by entity, the last <see cref="EarlierImportsScanned"/>): when and who; the file name only if recorded.
    /// </summary>
    private async Task<AirCurrentTableDto?> EarlierTableAsync(int sourcesDefId, long permitId, CancellationToken ct)
    {
        var records = await audit.ReadStructureChangesAsync(["cfg.RegistryDef"], sourcesDefId, EarlierImportsScanned, ct).ConfigureAwait(false);
        foreach (var record in records.Where(r => r.Operation == "AirLimitsImport"))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(record.NewJson ?? "{}");
                var root = json.RootElement;
                if (!root.TryGetProperty("permitId", out var p) || !p.TryGetInt64(out var id) || id != permitId)
                {
                    continue;
                }

                return Parse(record);
            }
            catch (System.Text.Json.JsonException)
            {
                // A damaged record is not this permit's answer.
            }
        }

        return null;
    }

    /// <summary>✎ D-569 (P-1123-1): the last table applied to the permit, from its own audit record (a seek by entity).</summary>
    private async Task<AirCurrentTableDto?> CurrentTableAsync(long permitId, CancellationToken ct)
    {
        if (permitId is <= 0 or > int.MaxValue)
        {
            return null;
        }

        var records = await audit.ReadStructureChangesAsync([AirLimitsSchema.CurrentTableAuditEntity], (int)permitId, 1, ct).ConfigureAwait(false);
        if (records.Count == 0)
        {
            return null;
        }

        return Parse(records[0]);
    }

    private static AirCurrentTableDto Parse(StructureChangeView last)
    {
        string? fileName = null;
        var sources = 0;
        var substances = 0;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(last.NewJson ?? "{}");
            var root = json.RootElement;
            if (root.TryGetProperty("fileName", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                fileName = f.GetString();
            }

            if (root.TryGetProperty("sources", out var s) && s.TryGetInt32(out var sn))
            {
                sources = sn;
            }

            if (root.TryGetProperty("substances", out var c) && c.TryGetInt32(out var cn))
            {
                substances = cn;
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // A damaged record still says when and who.
        }

        return new AirCurrentTableDto(fileName, last.ChangedAt, last.ChangedByDisplayName, sources, substances);
    }
}