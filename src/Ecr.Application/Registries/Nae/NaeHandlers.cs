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
    Hierarchy.SaveRegistryHierarchyHandler saveHierarchy,
    IUnitOfWork uow,
    IClock clock,
    IAuditWriter audit,
    IRegistryKeyStore keys,
    ICurrentUser currentUser)
{
    /// <summary>Operation of the one-time upgrade row in <c>aud.StructureChange</c>.</summary>
    internal const string UpgradeOperation = "UpgradeNodeTypes";

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
        (NaeTreeSchema.LevelSite, "Site", "Площадка", true),
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
            definition = await CreateAsync(code, system: null, ct).ConfigureAwait(false);
            created = true;
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

    /// <summary>
    /// ✎ AIRSYS02 (live-copy check 02.11: live has no NAE tree, so the AIR startup step refused and the AIR sources tables were never
    /// created): creates the registry EMPTY as the SYSTEM — the same definition, fields, business key and levels as
    /// <see cref="EnsureAsync"/>, no entries, author 0 «system» — in one transaction. An existing registry (compatible or not) is
    /// left exactly as it is and not validated here. Idempotent.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>internal</c>: only <see cref="Permits.AirLimits.AirTypeSystemProvisioner"/> (start) calls it; Ecr.Api does not see
    /// internals, so no controller can reach it (guard — <c>AirTypeStartupTests.No_controller_reaches_the_system_provisioner</c>).
    /// </remarks>
    /// <param name="code">Registry code.</param>
    /// <param name="system">The system actor (<see cref="Permits.PermitTypeActor.System"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether this call created the registry.</returns>
    internal async Task<bool> EnsureAsSystemAsync(string code, Permits.PermitTypeActor system, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false) is not null)
        {
            // NAE node types (human 03.11): an existing tree from before the node types is upgraded once, idempotently.
            await uow.ExecuteInTransactionAsync(token => UpgradeNodeTypesAsync(code, system.AuditUserId, token), ct).ConfigureAwait(false);
            await uow.ExecuteInTransactionAsync(token => MakeSiteOptionalAsync(code, system.AuditUserId, token), ct).ConfigureAwait(false);
            await uow.ExecuteInTransactionAsync(token => RepairScopeCodesAsync(code, system.AuditUserId, token), ct).ConfigureAwait(false);
            return false;
        }

        var created = false;
        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                created = false;
                if (await registries.FindDefinitionAsync(code, token).ConfigureAwait(false) is not null)
                {
                    return;
                }

                _ = await CreateAsync(code, system, token).ConfigureAwait(false);
                created = true;
            },
            ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Creates the registry with its fields, key and levels: as the request's user (<paramref name="system"/> null — the
    /// permission-checked handlers) or as the system (the author-taking methods, no permission check).</summary>
    private async Task<RegistryDef> CreateAsync(string code, Permits.PermitTypeActor? system, CancellationToken ct)
    {
        var registryName = new Dictionary<string, string> { ["en"] = "NAE structure", ["ru"] = "Структура НАЕ" };
        if (system is null)
        {
            _ = await create.HandleAsync(code, registryName, isTemporal: true, ct).ConfigureAwait(false);
        }
        else
        {
            _ = await create.ApplyAsync(code, registryName, isTemporal: true, system.Profile, ct).ConfigureAwait(false);
        }

        // Levels first (NAE node types, 03.11): the fields are bound to them in the same definition save below.
        var hierarchyRequest = new Hierarchy.SaveRegistryHierarchyRequest(
            true,
            [.. Levels.Select(l => new Hierarchy.RegistryLevelInput(
                l.Code, Name(l.En, l.Ru), l.Optional, l.Code == NaeTreeSchema.SelfNestingLevel))]);
        if (system is null)
        {
            await saveHierarchy.HandleAsync(code, hierarchyRequest, ct).ConfigureAwait(false);
        }
        else
        {
            var tracked = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"Registry {code} vanished after creation.");
            _ = await saveHierarchy.SaveForAuthorAsync(tracked, hierarchyRequest, system.AuditUserId, ct).ConfigureAwait(false);
        }

        // NaeCode is required on SRC only (Q3): the field is bound to SRC, so the requirement does not reach the group nodes.
        var fields = Fields.Select((f, i) => new RegistryFieldSaveDto(
            null, f.Code, Name(f.En, f.Ru), "String", i + 1, f.Code == NaeTreeSchema.NaeCodeField, f.IsKey, null, null,
            LevelCode: NaeTreeSchema.LevelByField[f.Code] ?? "")).ToList();
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
        var definitionDto = new SaveRegistryDefinitionDto(fields, [], "NAE tree: one-time provisioning", keys);
        var version = (await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false))!.DefinitionVersion.ToString(CultureInfo.InvariantCulture);
        if (system is null)
        {
            await saveDefinition.HandleAsync(code, definitionDto, ct, version).ConfigureAwait(false);
        }
        else
        {
            _ = await saveDefinition.SaveForAuthorAsync(code, definitionDto, system.AuditUserId, system.Profile, version, ct).ConfigureAwait(false);
        }

        return await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException($"Registry {code} vanished after creation.");
    }

    /// <summary>
    /// NAE node types (human 03.11): a tree provisioned before them gets the field bindings (<see cref="NaeTreeSchema.LevelByField"/>),
    /// <c>NaeCode</c> required (on SRC only) and SITE self-nesting — once. A tree where any NAE field is already bound or any level
    /// already nests is left alone (upgraded before, or configured by an administrator), so a second start changes nothing.
    /// </summary>
    /// <param name="code">Registry code.</param>
    /// <param name="userId">Author recorded on the definition (0 — system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether anything changed.</returns>
    internal async Task<bool> UpgradeNodeTypesAsync(string code, int userId, CancellationToken ct)
    {
        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        if (definition is null || !NaeTreeNodePolicy.IsNaeTree(definition))
        {
            return false;
        }

        var levels = (await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false))
            .ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        var naeFields = definition.Fields.Where(f => NaeTreeSchema.LevelByField.ContainsKey(f.Code)).ToList();
        var alreadyUpgraded = naeFields.Any(f => f.LevelId is not null) || levels.Values.Any(l => l.AllowsSelfNesting);

        // Correction (decision 03.11, «Аналіз»): a tree upgraded by the first node-types build has RegionCode/AreaCode bound to SRC;
        // they are unbound now (own identity of REGION/AREA nodes). Only a binding to SRC is touched — an administrator's own is not.
        var misbound = levels.TryGetValue(NaeTreeSchema.LevelSource, out var sourceLevel)
            ? naeFields.Where(f => NaeTreeSchema.LevelByField[f.Code] is null
                                   && NaeTreeSchema.OwnLevelOfScopeField(f.Code) is not null
                                   && f.LevelId == sourceLevel.Id).ToList()
            : [];
        if (alreadyUpgraded && misbound.Count == 0)
        {
            return false;
        }

        // Audit (follow-up 03.11): same snapshot helper as SaveDefinition; «before» is taken ahead of the changes below, «after» from
        // memory; the row is written in the caller's transaction, so it lands (or rolls back) together with the upgrade.
        var rules = await registries.ListRulesAsync(definition.Id, ct).ConfigureAwait(false);
        var existingKeys = await keys.ListKeysForUpdateAsync(definition.Id, ct).ConfigureAwait(false);
        var before = SaveRegistryDefinitionHandler.AuditSnapshot(definition, rules, existingKeys);

        foreach (var field in misbound)
        {
            field.BindToLevel(null);
        }

        if (!alreadyUpgraded)
        {
            foreach (var field in naeFields)
            {
                if (NaeTreeSchema.LevelByField[field.Code] is { } levelCode && levels.TryGetValue(levelCode, out var level))
                {
                    field.BindToLevel(level.Id);
                    if (string.Equals(field.Code, NaeTreeSchema.NaeCodeField, StringComparison.OrdinalIgnoreCase))
                    {
                        field.Update(field.NameL10n, field.Ordinal, isRequired: true);
                    }
                }
            }

            if (levels.TryGetValue(NaeTreeSchema.SelfNestingLevel, out var site))
            {
                site.AllowSelfNesting(true);
            }
        }

        definition.BumpDefinitionVersion();
        definition.MarkDefinitionUpdated(userId, clock.UtcNow);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.RegistryDef",
                EntityId: definition.Id,
                ChangeClass: ChangeClass.Guarded,
                Operation: UpgradeOperation,
                OldJson: before,
                NewJson: SaveRegistryDefinitionHandler.AuditSnapshot(definition, rules, existingKeys),
                ChangeReason: alreadyUpgraded
                    ? "NAE node types: RegionCode/AreaCode unbound from SRC on start"
                    : "NAE node types: one-time upgrade on start",
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Operation of the «SITE became optional» row in <c>aud.StructureChange</c> (AN-158).</summary>
    internal const string SiteOptionalOperation = "NaeSiteOptional";

    /// <summary>
    /// Human 04.11 («тип site не має бути обовʼязковим після area»): the SITE level of an existing tree stops being required, so an
    /// equipment group / NAE may hang straight under an area. Data only (no schema change); idempotent — a level that is already
    /// optional is not touched, so a second start changes nothing.
    /// </summary>
    /// <param name="code">Registry code.</param>
    /// <param name="userId">Author recorded in the audit (0 — system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether anything changed.</returns>
    internal async Task<bool> MakeSiteOptionalAsync(string code, int userId, CancellationToken ct)
    {
        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        if (definition is null || !NaeTreeNodePolicy.IsNaeTree(definition))
        {
            return false;
        }

        var site = (await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false))
            .FirstOrDefault(l => string.Equals(l.Code, NaeTreeSchema.LevelSite, StringComparison.OrdinalIgnoreCase));
        if (site is null || site.IsOptional)
        {
            return false;
        }

        site.Update(site.Ordinal, site.NameL10n, true);
        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.RegistryDef",
                EntityId: definition.Id,
                ChangeClass: ChangeClass.Guarded,
                Operation: SiteOptionalOperation,
                OldJson: "{\"site\":{\"isOptional\":false}}",
                NewJson: "{\"site\":{\"isOptional\":true}}",
                ChangeReason: "NAE structure: SITE level is optional (human 04.11)",
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Operation of the scope-code repair row in <c>aud.StructureChange</c> (AN-158).</summary>
    internal const string RepairOperation = "RepairScopeCodes";

    /// <summary>
    /// AN-158: a REGION/AREA node created without its own <c>RegionCode</c>/<c>AreaCode</c> (manual root, Excel/AF load before the fix)
    /// gets it backfilled from the code derived from its technical code — once derivation is stable, later renames cannot change it.
    /// A node whose derived code carries no letter or digit (a non-ASCII code flattened to <c>_____</c>) is left EMPTY: the original
    /// text is lost and is never invented — an administrator sets the code on the node (the 03.11 cascade updates the sources).
    /// Idempotent: a node that already stores its code is not touched, a second start changes nothing.
    /// </summary>
    /// <param name="code">Registry code.</param>
    /// <param name="userId">Author recorded in the audit (0 — system).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of codes written.</returns>
    internal async Task<int> RepairScopeCodesAsync(string code, int userId, CancellationToken ct)
    {
        var definition = await registries.FindDefinitionAsync(code, ct).ConfigureAwait(false);
        if (definition is null || !NaeTreeNodePolicy.IsNaeTree(definition))
        {
            return 0;
        }

        var levels = (await hierarchy.ListLevelsAsync(definition.Id, ct).ConfigureAwait(false))
            .ToDictionary(l => l.Code, StringComparer.OrdinalIgnoreCase);
        if (!levels.TryGetValue(NaeTreeSchema.LevelRegion, out var regionLevel) || !levels.TryGetValue(NaeTreeSchema.LevelArea, out var areaLevel))
        {
            return 0;
        }

        var nodes = (await hierarchy.ListLiveEntriesAsync(definition.Id, ct).ConfigureAwait(false)).ToDictionary(e => e.Id);
        var groups = nodes.Values.Where(n => n.LevelId == regionLevel.Id || n.LevelId == areaLevel.Id).ToList();
        if (groups.Count == 0)
        {
            return 0;
        }

        var fieldByCode = definition.Fields.ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase);
        var stored = (await registries.ListValuesForEntriesAsync([.. groups.Select(g => g.Id)], ct).ConfigureAwait(false))
            .Where(v => v.ValueString is { Length: > 0 })
            .Select(v => (v.RegistryEntryId, v.RegistryFieldDefId))
            .ToHashSet();

        bool Has(long entryId, string field) => stored.Contains((entryId, fieldByCode[field].Id));
        string RegionOf(RegistryEntry node)
        {
            for (var cursor = (long?)node.Id; cursor is { } id && nodes.TryGetValue(id, out var n); cursor = n.ParentEntryId)
            {
                if (n.LevelId == regionLevel.Id)
                {
                    return NaeTreeSchema.DerivedRegion(n.Code);
                }
            }

            return "";
        }

        var written = 0;
        var skipped = 0;
        foreach (var node in groups.OrderBy(n => n.Id))
        {
            var isRegion = node.LevelId == regionLevel.Id;
            var field = isRegion ? NaeTreeSchema.RegionField : NaeTreeSchema.AreaField;
            if (!fieldByCode.TryGetValue(field, out var def) || Has(node.Id, field))
            {
                continue;
            }

            var derived = isRegion ? NaeTreeSchema.DerivedRegion(node.Code) : NaeTreeSchema.DerivedArea(node.Code, RegionOf(node));
            if (derived.Length == 0)
            {
                skipped++;
                continue;
            }

            var value = new RegistryValue(node.Id, def.Id);
            value.Set(def.DataType, derived, def.UnitId);
            registries.AddValue(value);
            written++;
        }

        if (written == 0)
        {
            return 0;
        }

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.RegistryDef",
                EntityId: definition.Id,
                ChangeClass: ChangeClass.Guarded,
                Operation: RepairOperation,
                OldJson: null,
                NewJson: JsonSerializer.Serialize(new { written, leftEmpty = skipped }),
                ChangeReason: "NAE scope codes: own RegionCode/AreaCode backfilled on REGION/AREA nodes on start",
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        return written;
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
    NaeTreeNodeApplier applier,
    IAccessDecisionService access,
    ICurrentUser currentUser)
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

        // Highest ordinal among the live children of each existing parent (0 = roots): new siblings are appended after them.
        var maxOrdinalByParent = new Dictionary<long, int>();
        if (existingDefinition is not null)
        {
            var entries = (await registries.ListEntriesAsync(existingDefinition.Id, ct).ConfigureAwait(false)).Where(e => !e.IsDeleted).ToList();
            foreach (var group in entries.GroupBy(e => e.ParentEntryId ?? 0))
            {
                maxOrdinalByParent[group.Key] = group.Max(e => e.Ordinal);
            }

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
        var existingIdByKey = plan.Nodes
            .Where(n => existingByCode.ContainsKey(n.Code))
            .ToDictionary(n => n.Key, n => existingByCode[n.Code].Id, StringComparer.Ordinal);

        _ = await applier.WriteAsync(
            definition,
            levels,
            toCreate,
            existingIdByKey,
            maxOrdinalByParent,
            new NaeWriteAudit(
                "NaeAfImport", "registryAudit.naeAfImport", new { created = toCreate.Count, existing = existing.Values.Sum(), skipped }),
            userId,
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
