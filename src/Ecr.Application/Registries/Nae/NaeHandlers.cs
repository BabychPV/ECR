// src/Ecr.Application/Registries/Nae/NaeHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Nae;

/// <summary>
/// Makes sure the NAE tree registry exists with its levels, fields and composite key (NAE plan N2). Idempotent; an
/// existing registry is never edited — a registry that lacks a required field or level is refused (<c>422 naeTreeIncompatible</c>).
/// </summary>
public sealed class NaeTreeProvisioner(
    IRegistryStore registries,
    IRegistryHierarchyStore hierarchy,
    CreateRegistryHandler create,
    SaveRegistryDefinitionHandler saveDefinition,
    Hierarchy.SaveRegistryHierarchyHandler saveHierarchy)
{
    internal const string IncompatibleKey = "err.ECR-REG-0422.naeTreeIncompatible";

    private static readonly (string Code, string En, string Ru, bool IsKey)[] Fields =
    [
        (NaeTreeSchema.NaeCodeField, "NAE code", "Код НАЕ", true),
        (NaeTreeSchema.RegionField, "Region", "Регион", false),
        (NaeTreeSchema.AreaField, "Area", "Район", false),
        (NaeTreeSchema.AfPathField, "AF path", "Путь AF", false),
        (NaeTreeSchema.SourceTypeField, "Source type", "Тип источника", false),
        (NaeTreeSchema.PointNameField, "Emission point", "Точка выброса", false),
    ];

    private static readonly (string Code, string En, string Ru, bool Optional)[] Levels =
    [
        (NaeTreeSchema.LevelRegion, "Region", "Регион", false),
        (NaeTreeSchema.LevelArea, "Area", "Район", true),
        (NaeTreeSchema.LevelSite, "Site", "Площадка", false),
        (NaeTreeSchema.LevelEquipment, "Equipment group", "Группа оборудования", true),
        (NaeTreeSchema.LevelSource, "NAE source", "Источник НАЕ", false),
    ];

    /// <summary>Returns the registry, creating it when missing.</summary>
    /// <param name="code">Registry code.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registry, its levels by code, and whether this call created it.</returns>
    public async Task<(RegistryDef Definition, IReadOnlyDictionary<string, RegistryLevelDef> Levels, bool Created)> EnsureAsync(
        string code, CancellationToken ct)
    {
        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        var created = false;
        if (definition is null)
        {
            _ = await create.HandleAsync(
                code, new Dictionary<string, string> { ["en"] = "NAE structure", ["ru"] = "Структура НАЕ" }, isTemporal: true, ct)
                .ConfigureAwait(false);
            created = true;

            var fields = Fields.Select((f, i) => new RegistryFieldSaveDto(
                null, f.Code, Name(f.En, f.Ru), "String", i + 1, false, f.IsKey, null, null)).ToList();
            var keys = new List<RegistryKeySaveDto>
            {
                new(
                    null,
                    NaeTreeSchema.BusinessKeyCode,
                    Name("NAE business key", "Бизнес-ключ НАЕ"),
                    [NaeTreeSchema.NaeCodeField, NaeTreeSchema.RegionField, NaeTreeSchema.AreaField],
                    IsPrimary: false,
                    IgnoreCase: true,
                    IsActive: true),
            };
            await saveDefinition.HandleAsync(
                code,
                new SaveRegistryDefinitionDto(fields, [], "NAE tree: one-time provisioning", keys),
                ct,
                (await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false))!.DefinitionVersion.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

            await saveHierarchy.HandleAsync(
                code,
                new Hierarchy.SaveRegistryHierarchyRequest(
                    true, [.. Levels.Select(l => new Hierarchy.RegistryLevelInput(l.Code, Name(l.En, l.Ru), l.Optional))]),
                ct).ConfigureAwait(false);

            definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
                         ?? throw new InvalidOperationException($"Registry {code} vanished after creation.");
        }

        var haveFields = definition.Fields.Select(f => f.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var levelList = await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false);
        var byCode = levelList.ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var missing = Fields.Select(f => f.Code).Where(f => !haveFields.Contains(f))
            .Concat(NaeTreeSchema.LevelCodes.Where(l => !byCode.ContainsKey(l)))
            .ToList();
        if (!definition.IsHierarchical || missing.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-REG-0422",
                $"Registry «{code}» exists but is not an NAE tree (missing: {string.Join(", ", missing)}).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = IncompatibleKey,
                    ["registryCode"] = code,
                    ["missing"] = string.Join(", ", missing),
                });
        }

        return (definition, byCode, created);
    }

    private static LocalizedText Name(string en, string ru)
        => new(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru });
}

/// <summary>
/// One-time, create-only load of the NAE tree from the AF equipment tree. Permissions <c>Registry.EditDefinition</c> and
/// <c>Registry.EditData</c> (the first applied call also provisions the registry and so needs <c>Registry.Publish</c>).
/// </summary>
public sealed class ImportNaeFromAfHandler(
    INaeAfReader afReader,
    IRegistryStore registries,
    NaeTreeProvisioner provisioner,
    RegistryEntryWriter writer,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Most issues returned in the report.</summary>
    public const int MaxIssues = 200;

    internal const string RejectedKey = "err.ECR-REG-0422.naeImportRejected";

    /// <summary>Loads (or, for a dry run, only plans) the tree.</summary>
    /// <param name="request">Request.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<NaeAfImportReport> HandleAsync(ImportNaeFromAfRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        await PermissionCheck.RequireAsync(access, currentUser, Hierarchy.SaveRegistryHierarchyHandler.Permission, ct).ConfigureAwait(false);
        await PermissionCheck.RequireAsync(access, currentUser, "Registry.EditData", ct).ConfigureAwait(false);
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401",
                         "Anonymous requests do not change registries.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var code = EcrCode.Create(string.IsNullOrWhiteSpace(request.RegistryCode) ? NaeTreeSchema.RegistryCode : request.RegistryCode.Trim()).Value;
        var root = string.IsNullOrWhiteSpace(request.RootPath) ? NaeTreeSchema.DefaultAfRoot : request.RootPath.Trim();

        var snapshot = await afReader.ReadAsync(request.DataSourceId, root, ct).ConfigureAwait(false);
        var plan = NaeAfTreeBuilder.Build(snapshot, root);
        var issues = plan.Issues.ToList();

        // What already exists (by technical code and by business key): the load never changes or moves it.
        var existingDefinition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        var existingByCode = new Dictionary<string, RegistryEntry>(StringComparer.OrdinalIgnoreCase);
        var existingBusiness = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingDefinition is not null)
        {
            var entries = (await registries.ListEntriesAsync(existingDefinition.Id, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToList();
            foreach (var e in entries)
            {
                existingByCode[e.Code] = e;
            }

            existingBusiness = await BusinessKeysAsync(existingDefinition, entries, ct).ConfigureAwait(false);
        }

        var toCreate = new List<NaeTreeNodePlan>();
        var existing = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var node in plan.Nodes)
        {
            if (existingByCode.ContainsKey(node.Code))
            {
                existing[node.LevelCode] = existing.GetValueOrDefault(node.LevelCode) + 1;
            }
            else if (node.LevelCode == NaeTreeSchema.LevelSource && existingBusiness.Contains(BusinessKey(node.Fields)))
            {
                issues.Add(new NaeAfIssueDto(
                    "skipped", "existingElsewhere", node.Fields[NaeTreeSchema.AfPathField], "The business key already belongs to another node."));
            }
            else
            {
                toCreate.Add(node);
            }
        }

        // A new node whose parent was skipped (existing-elsewhere SRC has no children, so only groups matter) cannot happen:
        // groups are created or existing; sources are leaves.
        var counts = NaeTreeSchema.LevelCodes
            .Select(l => new NaeAfLevelCountDto(l, toCreate.Count(n => n.LevelCode == l), existing.GetValueOrDefault(l)))
            .ToList();
        var skipped = issues.Count(i => i.Severity == "skipped");

        if (request.DryRun || toCreate.Count == 0)
        {
            return Report(code, request.DryRun, false, false, snapshot, counts, skipped, issues);
        }

        var (definition, levels, provisioned) = await provisioner.EnsureAsync(code, ct).ConfigureAwait(false);
        ExternalRegistryGuard.EnsureManualEditAllowed(definition);
        var ids = existingByCode.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);
        var codeByKey = plan.Nodes.ToDictionary(n => n.Key, n => n.Code, StringComparer.Ordinal);

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                foreach (var level in NaeTreeSchema.LevelCodes)
                {
                    var batchNodes = toCreate.Where(n => n.LevelCode == level).ToList();
                    if (batchNodes.Count == 0)
                    {
                        continue;
                    }

                    var batch = new RegistryEntryWriteBatch(
                        definition.Id,
                        [.. batchNodes.Select(n => new RegistryEntryWrite(
                            n.Code, n.Fields.ToDictionary(f => f.Key, f => (object?)f.Value, StringComparer.Ordinal))
                        {
                            DisplayName = n.Display["en"],
                        })])
                    {
                        CreateOnly = true,
                    };
                    var result = await writer.WriteAsync(batch, token).ConfigureAwait(false);
                    if (result.Errors.Count > 0)
                    {
                        var first = result.Errors[0];
                        throw new BusinessRuleException(
                            "ECR-REG-0422",
                            $"NAE load rejected at {first.Key}: {first.MessageKey}.",
                            new Dictionary<string, object?>
                            {
                                ["messageKey"] = RejectedKey,
                                ["code"] = first.Key,
                                ["reason"] = first.MessageKey,
                            });
                    }

                    foreach (var row in result.Rows.Where(r => r.IsNew))
                    {
                        var node = batchNodes[row.Row - 1];
                        row.Entry.Rename(new LocalizedText(new Dictionary<string, string>(node.Display, StringComparer.OrdinalIgnoreCase)));
                        row.Entry.SetLevel(levels[node.LevelCode].Id);
                        row.Entry.SetOrdinal(node.Ordinal);
                        if (node.ParentKey is { } parentKey)
                        {
                            row.Entry.SetParent(ids[codeByKey[parentKey]]);
                        }

                        ids[node.Code] = row.Entry.Id;
                    }

                    await uow.SaveChangesAsync(token).ConfigureAwait(false);
                }

                await audit.WriteStructureChangeAsync(
                    new StructureChangeRecord(
                        ChangedAt: clock.UtcNow,
                        TemplateVersionId: 0,
                        EntityType: "cfg.RegistryDef",
                        EntityId: definition.Id,
                        ChangeClass: ChangeClass.Guarded,
                        Operation: "NaeAfImport",
                        OldJson: null,
                        NewJson: JsonSerializer.Serialize(new { created = toCreate.Count, existing = existing.Values.Sum(), skipped }),
                        ChangeReason: JsonSerializer.Serialize(new
                        {
                            k = "registryAudit.naeAfImport",
                            p = new { count = toCreate.Count.ToString(CultureInfo.InvariantCulture) },
                        }),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    token).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);

        return Report(code, false, true, provisioned, snapshot, counts, skipped, issues);
    }

    /// <summary>Business key text of a source node (the same value the composite key compares, case-insensitive).</summary>
    internal static string BusinessKey(IReadOnlyDictionary<string, string> fields)
        => $"{fields[NaeTreeSchema.NaeCodeField]}|{fields[NaeTreeSchema.RegionField]}|{fields[NaeTreeSchema.AreaField]}";

    private async Task<HashSet<string>> BusinessKeysAsync(RegistryDef definition, List<RegistryEntry> entries, CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wanted = new[] { NaeTreeSchema.NaeCodeField, NaeTreeSchema.RegionField, NaeTreeSchema.AreaField };
        var fieldIds = definition.Fields.Where(f => wanted.Contains(f.Code, StringComparer.OrdinalIgnoreCase))
            .ToDictionary(f => f.Id, f => f.Code);
        if (fieldIds.Count < wanted.Length || entries.Count == 0)
        {
            return result;
        }

        var values = await registries.ListValuesForEntriesAsync([.. entries.Select(e => e.Id)], ct).ConfigureAwait(false);
        foreach (var group in values.GroupBy(v => v.RegistryEntryId))
        {
            var byCode = group.Where(v => fieldIds.ContainsKey(v.RegistryFieldDefId))
                .ToDictionary(v => fieldIds[v.RegistryFieldDefId], v => v.ValueString ?? "", StringComparer.OrdinalIgnoreCase);
            if (wanted.All(byCode.ContainsKey) && wanted.All(w => byCode[w].Length > 0))
            {
                result.Add($"{byCode[wanted[0]]}|{byCode[wanted[1]]}|{byCode[wanted[2]]}");
            }
        }

        return result;
    }

    private static NaeAfImportReport Report(
        string code,
        bool dryRun,
        bool applied,
        bool provisioned,
        NaeAfSnapshot snapshot,
        List<NaeAfLevelCountDto> counts,
        int skipped,
        List<NaeAfIssueDto> issues)
        => new(code, dryRun, applied, provisioned, snapshot.Elements.Count, snapshot.IsComplete, counts, skipped, [.. issues.Take(MaxIssues)]);
}

/// <summary>"Where used" for a registry entry. Permission <c>Registry.View</c> or grant <c>Read</c>.</summary>
public sealed class GetRegistryEntryReferencesHandler(
    IRegistryStore registries,
    IRegistryEntryReferenceReader reader,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Permission to read registries.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Uses of an entry.</summary>
    /// <param name="registryCode">Registry code.</param>
    /// <param name="entryId">Entry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RegistryEntryReferencesDto> HandleAsync(string registryCode, long entryId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess.RequireAsync(access, currentUser, Permission, GrantLevel.Read, lookup, ct).ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        var entry = await registries.FindEntryAsync(entryId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
        {
            throw RegistryAccess.EntryNotFound(entryId, registryCode);
        }

        var items = await reader.ListAsync(definition.Id, entryId, ct).ConfigureAwait(false);
        return new RegistryEntryReferencesDto(definition.Code, entryId, items, items.Count);
    }
}
