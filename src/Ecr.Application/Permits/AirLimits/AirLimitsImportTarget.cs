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
        var (region, area) = AirScope.Parse(scope.ScopeKey);
        var permitId = long.Parse(scope.OwnerKey, CultureInfo.InvariantCulture);
        var tables = await Data.TablesAsync(ct).ConfigureAwait(false);

        var stored = await StoredAsync(tables, permitId, region, area, parentKeys, ct).ConfigureAwait(false);
        return [.. stored.Select(s => s.Parent)];
    }

    /// <inheritdoc />
    public async Task ApplyAsync(ImportApplyPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var (region, area) = AirScope.Parse(plan.Scope.ScopeKey);
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
                await CreateNaeAsync(tables, plan, region, area, token).ConfigureAwait(false);
                Data.Invalidate();

                // 2. The replaced rows (substances first: a source with substances is referenced).
                var keys = plan.Delete.Select(d => d.Key).ToList();
                var doomed = (await StoredAsync(tables, permitId, region, area, keys, token).ConfigureAwait(false))
                    .Where(s => plan.Delete.Any(d => d.OwnerKey == s.Parent.OwnerKey && d.Key == s.Parent.Key))
                    .ToList();
                foreach (var child in doomed.SelectMany(d => d.ChildIds))
                {
                    await deleter.HandleAsync(AirLimitsSchema.SubstancesRegistry, child, token).ConfigureAwait(false);
                }

                foreach (var source in doomed)
                {
                    await deleter.HandleAsync(AirLimitsSchema.SourcesRegistry, source.SourceId, token).ConfigureAwait(false);
                }

                Data.Invalidate();

                // 3. The new rows.
                var inserted = await InsertAsync(tables, plan, permitId, region, area, token).ConfigureAwait(false);

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
                            region,
                            area,
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
        AirTables tables, long permitId, string region, string area, IReadOnlyCollection<string> parentKeys, CancellationToken ct)
    {
        var naeIds = await Data.NaeByCodeAsync(tables, parentKeys, region, area, ct).ConfigureAwait(false);
        var permitField = PermitHeaderFields.PermitFieldCode;

        var own = await Data.EntriesPointingAtAsync(tables.Sources, permitField, [permitId], ct).ConfigureAwait(false);
        var sameNae = await Data.EntriesPointingAtAsync(tables.Sources, AirLimitsSchema.Nae, [.. naeIds.Values], ct).ConfigureAwait(false);
        var sources = await Data.ReadAsync(tables.Sources, [.. own, .. sameNae], ct).ConfigureAwait(false);

        // Own rows only inside the scope; other permits' rows are matched by NAE (which already carries region and area).
        var chosen = sources.Values
            .Where(r => r.Ref(permitField) == permitId
                ? AirLimitsSchema.NormalizeScope(r.Text(AirLimitsSchema.Region), "") == region
                  && AirLimitsSchema.NormalizeScope(r.Text(AirLimitsSchema.Area), AirLimitsSchema.NoArea) == area
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
            foreach (var (field, column) in AirLimitsSchema.ParentColumns)
            {
                values[field] = ColumnValue(row, column);
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
                foreach (var (field, column) in AirLimitsSchema.ChildColumns)
                {
                    cv[field] = child.Number(column);
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

        foreach (var creation in creations)
        {
            if (!long.TryParse(creation.UnderNode, NumberStyles.None, CultureInfo.InvariantCulture, out var parentId))
            {
                throw CreateUnderInvalid(creation.Code);
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

            var code = Unique(await IsFreeAsync(tables, $"N_{region}_{areaTag}_{creation.Code}", usedCodes, ct).ConfigureAwait(false));
            var name = creation.Values.GetValueOrDefault("sourceName") as string ?? creation.Code;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [NaeTreeSchema.NaeCodeField] = creation.Code,
                [NaeTreeSchema.RegionField] = region,
                [NaeTreeSchema.AreaField] = area,
                [NaeTreeSchema.SourceTypeField] = creation.Values.GetValueOrDefault("sourceType") as string,
                [NaeTreeSchema.PointNameField] = creation.Values.GetValueOrDefault("emissionPointName") as string,
            };

            var result = await writer.WriteAsync(
                new RegistryEntryWriteBatch(tables.Nae.Id, [new RegistryEntryWrite(code, fields) { DisplayName = name }]) { CreateOnly = true },
                ct).ConfigureAwait(false);
            if (result.Errors.Count > 0)
            {
                throw Rejected(creation.Code, result.Errors[0].MessageKey);
            }

            var entry = result.Rows[0].Entry;
            entry.Rename(new LocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = name, ["ru"] = name }));
            entry.SetLevel(source.Id);
            entry.SetParent(parentId);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task<string> IsFreeAsync(AirTables tables, string baseCode, HashSet<string> used, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var ch in baseCode)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? ch : '_');
        }

        var code = sb.Length > 60 ? sb.ToString(0, 60) : sb.ToString();
        var candidate = code;
        for (var i = 2; used.Contains(candidate) || await registries.FindEntryByCodeAsync(tables.Nae.Id, candidate, ct).ConfigureAwait(false) is not null; i++)
        {
            candidate = $"{code}_{i}";
        }

        used.Add(candidate);
        return candidate;
    }

    private static string Unique(string code) => code;

    // ---------- insert ----------

    private async Task<(int Sources, int Substances)> InsertAsync(
        AirTables tables, ImportApplyPlan plan, long permitId, string region, string area, CancellationToken ct)
    {
        if (plan.Insert.Count == 0)
        {
            return (0, 0);
        }

        var naeIds = await Data.NaeByCodeAsync(tables, [.. plan.Insert.Select(p => p.Key)], region, area, ct).ConfigureAwait(false);
        var substanceIds = await Data.SubstancesByCodeAsync(
            tables, [.. plan.Insert.SelectMany(p => p.Children).Select(c => c.Key).Distinct()], ct).ConfigureAwait(false);

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
                [AirLimitsSchema.Region] = region,
                [AirLimitsSchema.Area] = area,
                [AirLimitsSchema.SiteLabel] = parent.GroupLabel,
            };
            foreach (var (field, column) in AirLimitsSchema.ParentColumns)
            {
                values[column] = parent.Values.GetValueOrDefault(field);
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
                foreach (var (field, column) in AirLimitsSchema.ChildColumns)
                {
                    values[column] = child.Values.GetValueOrDefault(field);
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
