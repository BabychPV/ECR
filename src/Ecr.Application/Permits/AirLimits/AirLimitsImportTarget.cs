// src/Ecr.Application/Permits/AirLimits/AirLimitsImportTarget.cs
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Hierarchy;
using Ecr.Application.Registries.Nae;
using Ecr.Application.RegistryImport;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// The permit tables as the target of the generic registry import: reads what a replace touches and applies a plan in ONE
/// transaction (create the missing NAE nodes, delete the replaced rows, insert sources and substances).
/// </summary>
public sealed class AirLimitsImportTarget(
    AirLimitsSettingsAccessor accessor,
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    RegistryEntryWriter writer,
    DeleteRegistryEntryHandler deleter,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock) : IRegistryImportTarget
{
    private const int Batch = 500;

    private AirLimitsData Data => accessor.Data;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExistingParent>> ReadExistingAsync(
        ImportScope scope, IReadOnlyCollection<string> parentKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var air = AirImportScope.Parse(scope.ScopeKey);
        var permitId = long.Parse(scope.OwnerKey, CultureInfo.InvariantCulture);
        var tables = await Data.TablesAsync(ct).ConfigureAwait(false);

        var stored = await StoredAsync(tables, permitId, air, parentKeys, ct).ConfigureAwait(false);
        return [.. stored.Select(s => s.Parent)];
    }

    /// <inheritdoc />
    public async Task ApplyAsync(ImportApplyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var air = AirImportScope.Parse(plan.Scope.ScopeKey);
        var permitId = long.Parse(plan.Scope.OwnerKey, CultureInfo.InvariantCulture);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Anonymous requests do not change registries.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                Data.Invalidate();
                var tables = await Data.TablesAsync(token).ConfigureAwait(false);

                // D-211: a registry that AF owns is not written by hand (the permit tables never are; the guard is one for all writers).
                foreach (var def in new[] { tables.Sources, tables.Substances, tables.Nae })
                {
                    ExternalRegistryGuard.EnsureManualEditAllowed(def);
                }

                // 1. Missing NAE nodes first: the sources point at them.
                await CreateNaeAsync(tables, plan, air.NaeRegionForCreate, air.NaeAreaForCreate, token).ConfigureAwait(false);
                Data.Invalidate();

                // 2. The replaced rows (substances first: a source with substances is referenced).
                var keys = plan.Delete.Select(d => d.Key).ToList();
                var doomed = (await StoredAsync(tables, permitId, air, keys, token).ConfigureAwait(false))
                    .Where(s => plan.Delete.Any(d => d.OwnerKey == s.Parent.OwnerKey && d.Key == s.Parent.Key))
                    .ToList();
                // Set-based: substances first (a source with substances is referenced), then the sources, one batch each.
                await deleter.HandleManyAsync(AirLimitsSchema.SubstancesRegistry, [.. doomed.SelectMany(d => d.ChildIds)], token).ConfigureAwait(false);
                await deleter.HandleManyAsync(AirLimitsSchema.SourcesRegistry, [.. doomed.Select(d => d.SourceId)], token).ConfigureAwait(false);
                uow.ReleaseSavedRegistryRows();
                Data.Invalidate();

                // 3. The new rows. ✎ F13-p198 P3-3: values of ADDED columns the profile does not map are carried over from this
                // permit's replaced row with the same NAE (and substance): a re-import must not wipe what the file does not carry.
                var own = permitId.ToString(CultureInfo.InvariantCulture);
                var carry = new Carry(
                    doomed.Where(d => d.Parent.OwnerKey == own)
                        .GroupBy(d => d.Parent.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().Parent.Values, StringComparer.Ordinal),
                    doomed.Where(d => d.Parent.OwnerKey == own)
                        .SelectMany(d => d.Parent.Children.Select(c => (Key: $"{d.Parent.Key}/{c.Key}", c.Values)))
                        .GroupBy(c => c.Key, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key, g => g.First().Values, StringComparer.Ordinal));
                var inserted = await InsertAsync(tables, plan, permitId, air, carry, token).ConfigureAwait(false);

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "cfg.RegistryDef",
                        EntityId: tables.Sources.Id,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "AirLimitsImport",
                        OldJson: JsonSerializer.Serialize(new { replaced = doomed.Count, owners = plan.Delete.Select(d => d.OwnerKey).Distinct().Count() }),
                        NewJson: JsonSerializer.Serialize(new
                        {
                            permitId,
                            sources = inserted.Sources,
                            substances = inserted.Substances,
                            createdNae = plan.CreateReferences.Count,
                            region = air.Region.Code,
                            area = air.Area?.Code,
                            naeRegion = air.NaeRegion,
                            naeArea = air.NaeArea,
                            file = plan.FileSha256,
                            profile = plan.ProfileCode,
                            fingerprint = plan.Fingerprint,
                        }),
                        ChangeReason: JsonSerializer.Serialize(new
                        {
                            k = "registryAudit.airLimitsImport",
                            p = new
                            {
                                sources = inserted.Sources.ToString(CultureInfo.InvariantCulture),
                                substances = inserted.Substances.ToString(CultureInfo.InvariantCulture),
                            },
                        }),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    private sealed record StoredSource(ExistingParent Parent, long SourceId, IReadOnlyList<long> ChildIds);

    /// <summary>The permit's rows in the scope plus the rows of OTHER permits for the same NAE.</summary>
    private async Task<List<StoredSource>> StoredAsync(
        AirTables tables, long permitId, AirImportScope air, IReadOnlyCollection<string> parentKeys, CancellationToken ct)
    {
        var naeIds = await Data.NaeByCodeAsync(tables, parentKeys, air.NaeRegion, air.NaeArea, ct).ConfigureAwait(false);
        var permitField = PermitHeaderFields.PermitFieldCode;

        var own = await Data.EntriesPointingAtAsync(tables.Sources, permitField, [permitId], ct).ConfigureAwait(false);
        var sameNae = await Data.EntriesPointingAtAsync(tables.Sources, AirLimitsSchema.Nae, [.. naeIds.Values], ct).ConfigureAwait(false);
        var sources = await Data.ReadAsync(tables.Sources, [.. own, .. sameNae], ct).ConfigureAwait(false);

        // ✎ AIR-ON-AIR: own rows only inside the scope (the same region / area VALUES stored on the rows — one AIR permit holds
        // several region/area blocks); other permits' rows are matched by NAE (which already carries region and area), Q2.
        var chosen = sources.Values
            .Where(r => r.Ref(permitField) == permitId
                ? air.Holds(r)
                : r.Ref(AirLimitsSchema.Nae) is { } n && naeIds.ContainsValue(n))
            .ToList();
        if (chosen.Count == 0)
        {
            return [];
        }

        var naeCodes = await Data.NaeCodesAsync(tables, [.. chosen.Select(c => c.Ref(AirLimitsSchema.Nae)).OfType<long>().Distinct()], ct).ConfigureAwait(false);
        var permitIds = chosen.Select(c => c.Ref(permitField)).OfType<long>().Distinct().ToList();
        var permits = await Data.ReadAsync(tables.Header, permitIds, ct).ConfigureAwait(false);

        var childIds = await Data.EntriesPointingAtAsync(tables.Substances, AirLimitsSchema.Source, [.. chosen.Select(c => c.Entry.Id)], ct).ConfigureAwait(false);
        var children = await Data.ReadAsync(tables.Substances, childIds, ct).ConfigureAwait(false);
        var substanceCodes = await Data.SubstanceCodesAsync(
            tables, [.. children.Values.Select(c => c.Ref(AirLimitsSchema.Substance)).OfType<long>().Distinct()], ct).ConfigureAwait(false);

        var parentColumns = AirLimitsSchema.ParentColumnsOf(tables.Sources);
        var childColumns = AirLimitsSchema.ChildColumnsOf(tables.Substances);
        var result = new List<StoredSource>();
        foreach (var row in chosen)
        {
            if (row.Ref(AirLimitsSchema.Nae) is not { } naeId || !naeCodes.TryGetValue(naeId, out var naeCode))
            {
                continue;
            }

            var owner = row.Ref(permitField)!.Value;
            var label = permits.TryGetValue(owner, out var permit) ? permit.Text("Permit_Number") : null;

            var values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["naeCode"] = naeCode };
            foreach (var (field, column) in parentColumns)
            {
                values[field] = AirLimitsSchema.ParentColumns.ContainsKey(field) ? ColumnValue(row, column) : AddedValue(row, tables.Sources, column);
            }

            var kids = new List<ExistingChild>();
            var kidIds = new List<long>();
            foreach (var child in children.Values.Where(c => c.Ref(AirLimitsSchema.Source) == row.Entry.Id))
            {
                kidIds.Add(child.Entry.Id);
                if (child.Ref(AirLimitsSchema.Substance) is not { } sid || !substanceCodes.TryGetValue(sid, out var code))
                {
                    continue;
                }

                var cv = new Dictionary<string, object?>(StringComparer.Ordinal) { ["substanceCode"] = code };
                foreach (var (field, column) in childColumns)
                {
                    // Built-in substance columns are numbers; an added column keeps its own type (number or text).
                    cv[field] = AirLimitsSchema.ChildColumns.ContainsKey(field) ? child.Number(column) : AddedValue(child, tables.Substances, column);
                }

                kids.Add(new ExistingChild(code, cv));
            }

            result.Add(new StoredSource(
                new ExistingParent(owner.ToString(CultureInfo.InvariantCulture), label, naeCode, values, kids), row.Entry.Id, kidIds));
        }

        return result;
    }

    private static object? ColumnValue(AirRow row, string column)
        => row.Values.TryGetValue(column, out var v) ? (object?)v.Numeric ?? v.Text : null;

    /// <summary>An added column's value by its own type (date and bool too, not only number/text) — compared and carried over.</summary>
    private static object? AddedValue(AirRow row, Domain.Entities.Configuration.RegistryDef table, string column)
        => table.Fields.FirstOrDefault(f => string.Equals(f.Code, column, StringComparison.OrdinalIgnoreCase)) is { } field
            ? row.Value(field)
            : ColumnValue(row, column);

    // ---------- NAE creation ----------

    private async Task CreateNaeAsync(AirTables tables, ImportApplyPlan plan, string region, string area, CancellationToken ct)
    {
        var creations = plan.CreateReferences.Where(c => c.Kind == INaeResolver.NaeKind).ToList();
        if (creations.Count == 0)
        {
            return;
        }

        var levels = await hierarchy.ListLevelsAsync(tables.Nae.Id, ct).ConfigureAwait(false);
        var source = RegistryHierarchyPlacement.LevelByCode(levels, NaeTreeSchema.LevelSource);
        var areaTag = area == AirLimitsSchema.NoArea ? "NA" : area;
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Parents are validated and placed once per DISTINCT parent node, not once per created node.
        var checkedParents = new HashSet<long>();
        foreach (var creation in creations)
        {
            if (!long.TryParse(creation.UnderNode, NumberStyles.None, CultureInfo.InvariantCulture, out var parentId))
            {
                throw CreateUnderInvalid(creation.Code);
            }

            if (!checkedParents.Add(parentId))
            {
                continue;
            }

            var parent = await registries.FindEntryAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null || parent.IsDeleted || parent.RegistryDefId != tables.Nae.Id)
            {
                throw CreateUnderInvalid(creation.Code);
            }

            try
            {
                await RegistryHierarchyPlacement.EnsureAsync(hierarchy, tables.Nae.Id, levels, null, parentId, source, ct).ConfigureAwait(false);
            }
            catch (BusinessRuleException)
            {
                throw CreateUnderInvalid(creation.Code);
            }
        }

        // Codes: one lookup of every wanted base code; only a taken one walks its suffixes one by one.
        var wanted = creations.Select(c => BaseCode(region, areaTag, c.Code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var taken = (await registries.FindEntriesByCodesAsync(tables.Nae.Id, wanted, ct).ConfigureAwait(false))
            .Select(e => e.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A base code is answered by the one lookup above; only a suffixed candidate asks the database.
        async Task<bool> IsTakenAsync(string candidate, bool isBase)
            => usedCodes.Contains(candidate)
               || (isBase
                   ? taken.Contains(candidate)
                   : await registries.FindEntryByCodeAsync(tables.Nae.Id, candidate, ct).ConfigureAwait(false) is not null);

        var writes = new List<RegistryEntryWrite>(creations.Count);
        foreach (var creation in creations)
        {
            var baseCode = BaseCode(region, areaTag, creation.Code);
            var code = baseCode;
            for (var i = 2; await IsTakenAsync(code, code == baseCode).ConfigureAwait(false); i++)
            {
                code = $"{baseCode}_{i}";
            }

            usedCodes.Add(code);
            var name = creation.Values.GetValueOrDefault("sourceName") as string ?? creation.Code;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [NaeTreeSchema.NaeCodeField] = creation.Code,
                [NaeTreeSchema.RegionField] = region,
                [NaeTreeSchema.AreaField] = area,
                [NaeTreeSchema.SourceTypeField] = creation.Values.GetValueOrDefault("sourceType") as string,
                [NaeTreeSchema.PointNameField] = creation.Values.GetValueOrDefault("emissionPointName") as string,
            };

            writes.Add(new RegistryEntryWrite(code, fields) { DisplayName = name });
        }

        var result = await writer.WriteAsync(new RegistryEntryWriteBatch(tables.Nae.Id, writes) { CreateOnly = true }, ct).ConfigureAwait(false);
        if (result.Errors.Count > 0)
        {
            throw Rejected(creations[result.Errors[0].Row - 1].Code, result.Errors[0].MessageKey);
        }

        // Ordinals: appended after the live siblings of the parent, in file order.
        var nextOrdinal = (await registries.ListEntriesAsync(tables.Nae.Id, ct).ConfigureAwait(false))
            .Where(e => !e.IsDeleted && e.ParentEntryId is not null)
            .GroupBy(e => e.ParentEntryId!.Value)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Ordinal));
        foreach (var row in result.Rows)
        {
            var creation = creations[row.Row - 1];
            var name = creation.Values.GetValueOrDefault("sourceName") as string ?? creation.Code;
            row.Entry.Rename(new LocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = name, ["ru"] = name }));
            row.Entry.SetLevel(source.Id);
            var under = long.Parse(creation.UnderNode!, CultureInfo.InvariantCulture);
            row.Entry.SetParent(under);
            nextOrdinal[under] = nextOrdinal.GetValueOrDefault(under) + 1;
            row.Entry.SetOrdinal(nextOrdinal[under]);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        uow.ReleaseSavedRegistryRows();
    }

    private static string BaseCode(string region, string areaTag, string naeCode)
    {
        var sb = new StringBuilder();
        foreach (var ch in $"N_{region}_{areaTag}_{naeCode}")
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }

        return sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
    }

    // ---------- insert ----------

    /// <summary>Stored values of the replaced own rows: NAE → source values, «NAE/substance» → substance values (by field name).</summary>
    private sealed record Carry(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Sources,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Substances);

    private async Task<(int Sources, int Substances)> InsertAsync(
        AirTables tables, ImportApplyPlan plan, long permitId, AirImportScope air, Carry carry, CancellationToken ct)
    {
        if (plan.Insert.Count == 0)
        {
            return (0, 0);
        }

        // The same NAE scope the resolver used (null = any); nodes just created lie inside it by construction.
        var naeIds = await Data.NaeByCodeAsync(tables, [.. plan.Insert.Select(p => p.Key)], air.NaeRegion, air.NaeArea, ct).ConfigureAwait(false);
        var substanceIds = await Data.SubstancesByCodeAsync(
            tables, [.. plan.Insert.SelectMany(p => p.Children).Select(c => c.Key).Distinct()], ct).ConfigureAwait(false);

        var parentColumns = AirLimitsSchema.ParentColumnsOf(tables.Sources);
        var childColumns = AirLimitsSchema.ChildColumnsOf(tables.Substances);
        var sourceWrites = new List<RegistryEntryWrite>();
        foreach (var parent in plan.Insert)
        {
            if (!naeIds.TryGetValue(parent.Key, out var naeId))
            {
                throw Rejected(parent.Key, "naeMissing");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [PermitHeaderFields.PermitFieldCode] = permitId,
                [AirLimitsSchema.Nae] = naeId,
                [AirLimitsSchema.Region] = air.Region.Stored,
                [AirLimitsSchema.Area] = air.Area?.Stored,
                [AirLimitsSchema.SiteLabel] = parent.GroupLabel,
            };
            var keptSource = carry.Sources.GetValueOrDefault(parent.Key);
            foreach (var (field, column) in parentColumns)
            {
                if (parent.Values.TryGetValue(field, out var value) || AirLimitsSchema.ParentColumns.ContainsKey(field))
                {
                    values[column] = value;
                }
                else if (keptSource?.GetValueOrDefault(field) is { } kept)
                {
                    // An added column the profile does not map: the replaced row's value stays (P3-3).
                    values[column] = kept;
                }
            }

            sourceWrites.Add(new RegistryEntryWrite(string.Empty, values) { DisplayName = $"{parent.Key} {parent.Values.GetValueOrDefault("sourceName")}" });
        }

        var sourceIds = new List<long>();
        foreach (var chunk in sourceWrites.Chunk(Batch))
        {
            var result = await writer.WriteAsync(new RegistryEntryWriteBatch(tables.Sources.Id, chunk), ct).ConfigureAwait(false);
            if (result.Errors.Count > 0)
            {
                var first = result.Errors[0];
                throw Rejected(plan.Insert[sourceIds.Count + first.Row - 1].Key, first.MessageKey);
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            sourceIds.AddRange(result.Rows.OrderBy(r => r.Row).Select(r => r.Entry.Id));
            uow.ReleaseSavedRegistryRows();
        }

        var childWrites = new List<(string Key, RegistryEntryWrite Write)>();
        for (var i = 0; i < plan.Insert.Count; i++)
        {
            foreach (var child in plan.Insert[i].Children)
            {
                if (!substanceIds.TryGetValue(child.Key, out var substanceId))
                {
                    throw Rejected($"{plan.Insert[i].Key}/{child.Key}", "substanceMissing");
                }

                var values = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [PermitHeaderFields.PermitFieldCode] = permitId,
                    [AirLimitsSchema.Source] = sourceIds[i],
                    [AirLimitsSchema.Substance] = substanceId,
                };
                var keptSubstance = carry.Substances.GetValueOrDefault($"{plan.Insert[i].Key}/{child.Key}");
                foreach (var (field, column) in childColumns)
                {
                    if (child.Values.TryGetValue(field, out var value) || AirLimitsSchema.ChildColumns.ContainsKey(field))
                    {
                        values[column] = value;
                    }
                    else if (keptSubstance?.GetValueOrDefault(field) is { } kept)
                    {
                        values[column] = kept;
                    }
                }

                childWrites.Add(($"{plan.Insert[i].Key}/{child.Key}", new RegistryEntryWrite(string.Empty, values) { DisplayName = child.Key }));
            }
        }

        var done = 0;
        foreach (var chunk in childWrites.Chunk(Batch))
        {
            var result = await writer.WriteAsync(
                new RegistryEntryWriteBatch(tables.Substances.Id, [.. chunk.Select(c => c.Write)]), ct).ConfigureAwait(false);
            if (result.Errors.Count > 0)
            {
                var first = result.Errors[0];
                throw Rejected(chunk[first.Row - 1].Key, first.MessageKey);
            }

            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            done += chunk.Length;
            uow.ReleaseSavedRegistryRows();
        }

        return (sourceIds.Count, done);
    }

    private static BusinessRuleException Rejected(string key, string reason)
        => new(
            "ECR-IMP-0422",
            $"The import was refused at {key}: {reason}. Nothing was written.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = AirLimitsKeys.WriteRejected,
                ["key"] = key,
                ["reason"] = reason,
            });

    private static BusinessRuleException CreateUnderInvalid(string code)
        => new(
            "ECR-IMP-0422",
            $"NAE {code} cannot be created under the chosen node.",
            new Dictionary<string, object?> { ["messageKey"] = AirLimitsKeys.CreateUnderInvalid, ["key"] = code });
}
