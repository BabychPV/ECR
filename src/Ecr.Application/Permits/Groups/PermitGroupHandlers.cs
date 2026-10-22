// src/Ecr.Application/Permits/Groups/PermitGroupHandlers.cs
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Groups;

/// <summary>Збирання відповіді з групи й перевірки запитів (спільне для читання й змін).</summary>
internal static class PermitGroupAssembler
{
    /// <summary>Найбільша довжина назви групи на мову.</summary>
    internal const int MaxNameChars = 200;

    /// <summary>Найбільша довжина опису групи на мову.</summary>
    internal const int MaxDescriptionChars = 1000;

    /// <summary>Група у відповідь.</summary>
    internal static PermitGroupDto ToDto(
        PermitGroup group,
        IReadOnlyDictionary<int, PermitTypeSummary> typesById,
        IReadOnlyDictionary<int, RegistryTemplateUsage> usage)
    {
        var types = group.Members
            .OrderBy(m => m.Ordinal)
            .Select(m => typesById.GetValueOrDefault(m.PermitTypeDefId))
            .Where(t => t is not null)
            .Select(t => new PermitGroupTypeDto(
                t!.Code,
                new Dictionary<string, string>(t.NameL10n.Values),
                PermitTypeCodec.CategoryName(t.Category),
                PermitHeaderFields.HeaderRegistryCode(t.Code)))
            .ToList();

        var used = usage.GetValueOrDefault(group.RegistryDefId);

        return new PermitGroupDto(
            group.Code,
            new Dictionary<string, string>(group.NameL10n.Values),
            new Dictionary<string, string>(group.DescriptionL10n.Values),
            group.IsActive,
            PermitGroupErrors.RegistryCode(group.Code),
            group.RegistryDefId,
            types,
            used?.Columns ?? 0,
            used?.Templates ?? 0,
            Convert.ToBase64String(group.RowVersion),
            group.UpdatedAt);
    }

    /// <summary>Назва: хоча б одна мова непорожня, кожна не довша за межу.</summary>
    internal static LocalizedText Name(IReadOnlyDictionary<string, string>? name)
    {
        if (name is null || !name.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
        {
            throw PermitGroupErrors.Invalid("Назва групи дозволів обов'язкова.", "err.ECR-PRM-0422.permitGroupNameRequired");
        }

        RequireLength(name, MaxNameChars, "name");
        return Text(name);
    }

    /// <summary>Опис: необов'язковий, кожна мова не довша за межу.</summary>
    internal static LocalizedText Description(IReadOnlyDictionary<string, string>? description)
    {
        if (description is null)
        {
            return new LocalizedText();
        }

        RequireLength(description, MaxDescriptionChars, "description");
        return Text(description);
    }

    /// <summary>Код групи: код каталогу, а <c>PRMG_</c> + код вміщається в код довідника.</summary>
    internal static EcrCode Code(string? code)
    {
        if (!EcrCode.TryCreate(code, out var parsed) || !EcrCode.TryCreate(PermitGroupErrors.RegistryCode(parsed.Value), out _))
        {
            throw PermitGroupErrors.Invalid(
                $"Код групи «{code}» недопустимий.", "err.ECR-PRM-0422.permitGroupCodeInvalid", ("code", code ?? string.Empty));
        }

        return parsed;
    }

    /// <summary>Склад із кодів типів: типи існують, опубліковані, без повторів; порядок - за списком.</summary>
    internal static async Task<IReadOnlyList<PermitTypeSummary>> MembersAsync(
        IReadOnlyList<string>? typeCodes, IPermitTypeStore permits, CancellationToken ct)
    {
        if (typeCodes is null || typeCodes.Count == 0)
        {
            return [];
        }

        var all = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Code, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new List<PermitTypeSummary>(typeCodes.Count);

        foreach (var typeCode in typeCodes)
        {
            if (!seen.Add(typeCode ?? string.Empty))
            {
                throw PermitGroupErrors.Invalid(
                    $"Тип «{typeCode}» повторюється в групі.", "err.ECR-PRM-0422.permitGroupMemberDuplicate", ("typeCode", typeCode ?? string.Empty));
            }

            if (typeCode is null || !all.TryGetValue(typeCode, out var type))
            {
                throw PermitGroupErrors.Invalid(
                    $"Типу дозволу «{typeCode}» не існує.", "err.ECR-PRM-0422.permitGroupMemberUnknown", ("typeCode", typeCode ?? string.Empty));
            }

            if (type.Status != PermitTypeStatus.Published)
            {
                throw PermitGroupErrors.Invalid(
                    $"Тип «{typeCode}» ще не опублікований: до групи входять лише опубліковані типи.",
                    "err.ECR-PRM-0422.permitGroupMemberNotPublished",
                    ("typeCode", typeCode));
            }

            members.Add(type);
        }

        return members;
    }

    /// <summary>Склад як JSON для аудиту (коди типів у порядку).</summary>
    internal static string MembersJson(PermitGroup group, IReadOnlyDictionary<int, PermitTypeSummary> typesById)
        => JsonSerializer.Serialize(new
        {
            group.Code,
            group.IsActive,
            Types = group.Members.OrderBy(m => m.Ordinal).Select(m => typesById.GetValueOrDefault(m.PermitTypeDefId)?.Code ?? $"#{m.PermitTypeDefId}"),
        });

    private static LocalizedText Text(IReadOnlyDictionary<string, string> values)
        => new(values.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.OrdinalIgnoreCase));

    private static void RequireLength(IReadOnlyDictionary<string, string> values, int max, string item)
    {
        foreach (var (language, text) in values)
        {
            if (text is { } value && value.Length > max)
            {
                throw PermitGroupErrors.Invalid(
                    $"Текст «{item}» ({language}) довший за {max} символів.",
                    "err.ECR-PRM-0422.permitGroupTextTooLong",
                    ("item", item),
                    ("max", max.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }
}

/// <summary>Перелік груп. Право <c>Registry.View</c>; групи з забороною S18 на реєстр групи не повертаються.</summary>
public sealed class ListPermitGroupsHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає всі групи (їх десятки: без сторінок).</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<PermitGroupDto>> HandleAsync(CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var list = await groups.ListAsync(ct).ConfigureAwait(false);
        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var usage = await registries.CountTemplateUsageAsync(ct).ConfigureAwait(false);

        return [.. list.Where(g => !RegistryAccess.IsDenied(profile, g.RegistryDefId)).Select(g => PermitGroupAssembler.ToDto(g, types, usage))];
    }
}

/// <summary>Одна група за кодом. Право <c>Registry.View</c>; заборона S18 на реєстр групи - 404 «як неіснуюча».</summary>
public sealed class GetPermitGroupHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає групу.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0405</c>.</exception>
    public async Task<PermitGroupDto> HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var group = await groups.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (group is null || RegistryAccess.IsDenied(profile, group.RegistryDefId))
        {
            throw PermitGroupErrors.NotFound(code);
        }

        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var usage = await registries.CountTemplateUsageAsync(ct).ConfigureAwait(false);

        return PermitGroupAssembler.ToDto(group, types, usage);
    }
}

/// <summary>
/// Створює групу: прихований реєстр <c>PRMG_&lt;КОД&gt;</c> без полів і записів + рядок групи + склад, однією транзакцією.
/// Право <c>Permit.EditType</c>.
/// </summary>
public sealed class CreatePermitGroupHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    PermitGroupSnapshotCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну типів дозволів (і їхніх груп).</summary>
    public const string Permission = "Permit.EditType";

    /// <summary>Створює групу.</summary>
    /// <param name="request">Код, назва, опис, початковий склад.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-4097</c> код зайнятий; <c>ECR-PRM-0422</c> назва, опис, код, склад.</exception>
    public async Task<PermitGroupDto> HandleAsync(CreatePermitGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var code = PermitGroupAssembler.Code(request.Code);
        var name = PermitGroupAssembler.Name(request.NameL10n);
        var description = PermitGroupAssembler.Description(request.DescriptionL10n);
        var members = await PermitGroupAssembler.MembersAsync(request.TypeCodes, permits, ct).ConfigureAwait(false);

        if (await groups.FindByCodeAsync(code.Value, ct).ConfigureAwait(false) is not null)
        {
            throw PermitGroupErrors.Duplicate(code.Value);
        }

        var registryCode = PermitGroupErrors.RegistryCode(code.Value);
        if (await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false) is not null)
        {
            throw PermitGroupErrors.RegistryCodeTaken(registryCode);
        }

        PermitGroup? group = null;
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // Реєстр-«ідентичність» групи: без полів і записів, не темпоральний; ревізія піднімається разом із групою.
            var registry = new RegistryDef(EcrCode.Create(registryCode), name, isTemporal: false);
            registry.BumpDataRevision();
            registries.AddDefinition(registry);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            group = new PermitGroup(code, name, description, registry.Id, userId, now);
            group.ReplaceMembers([.. members.Select(m => m.Id)], userId, now);
            groups.Add(group);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: now,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitGroup",
                    EntityId: group.Id,
                    ChangeClass: ChangeClass.Safe,
                    Operation: "CreatePermitGroup",
                    OldJson: null,
                    NewJson: PermitGroupAssembler.MembersJson(group, members.ToDictionary(m => m.Id)),
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        cache.Invalidate();

        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        return PermitGroupAssembler.ToDto(group!, types, new Dictionary<int, RegistryTemplateUsage>());
    }
}

/// <summary>
/// Змінює назву, опис і активність групи (деактивація - <c>IsActive = false</c>). Право <c>Permit.EditType</c>.
/// Назва й активність дзеркаляться на прихований реєстр групи; <c>DataRevision</c> реєстру піднімається в тій самій транзакції.
/// </summary>
public sealed class UpdatePermitGroupHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    PermitGroupSnapshotCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну типів дозволів (і їхніх груп).</summary>
    public const string Permission = "Permit.EditType";

    /// <summary>Змінює групу.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="request">Назва, опис, активність і токен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0405</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4099</c>.</exception>
    public async Task<PermitGroupDto> HandleAsync(string code, UpdatePermitGroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var group = await groups.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (group is null || RegistryAccess.IsDenied(profile, group.RegistryDefId))
        {
            throw PermitGroupErrors.NotFound(code);
        }

        PermitGroupErrors.RequireRowVersion(group, request.RowVersion);

        var name = PermitGroupAssembler.Name(request.NameL10n);
        var description = PermitGroupAssembler.Description(request.DescriptionL10n);
        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var before = PermitGroupAssembler.MembersJson(group, types);
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            group.Update(name, description, request.IsActive, userId, now);

            if (await registries.FindDefinitionByIdAsync(group.RegistryDefId, innerCt).ConfigureAwait(false) is { } registry)
            {
                registry.Rename(name);
                registry.SetActive(request.IsActive);
                registry.BumpDataRevision();
            }

            try
            {
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.CellConflict)
            {
                throw PermitGroupErrors.Changed(group.Code, group.RowVersion);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: now,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitGroup",
                    EntityId: group.Id,
                    ChangeClass: request.IsActive ? ChangeClass.Safe : ChangeClass.Guarded,
                    Operation: "UpdatePermitGroup",
                    OldJson: before,
                    NewJson: PermitGroupAssembler.MembersJson(group, types),
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        cache.Invalidate();

        var usage = await registries.CountTemplateUsageAsync(ct).ConfigureAwait(false);
        return PermitGroupAssembler.ToDto(group, types, usage);
    }
}

/// <summary>
/// Замінює склад групи цілком (<c>PUT permit-groups/{code}/types</c>): єдиний шлях зміни складу. Право
/// <c>Permit.EditType</c>. Уже обрані значення лишаються в комірках; нові значення типу поза складом відхиляються
/// як «немає запису» (PATCH перевіряє лише змінені значення).
/// </summary>
public sealed class ReplacePermitGroupTypesHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    PermitGroupSnapshotCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну типів дозволів (і їхніх груп).</summary>
    public const string Permission = "Permit.EditType";

    /// <summary>Замінює склад.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="request">Коди типів у порядку показу і токен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0405</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4099</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> (<c>groupMemberUnknown</c>, <c>groupMemberNotPublished</c>, <c>groupMemberDuplicate</c>).</exception>
    public async Task<PermitGroupDto> HandleAsync(string code, ReplacePermitGroupTypesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var group = await groups.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (group is null || RegistryAccess.IsDenied(profile, group.RegistryDefId))
        {
            throw PermitGroupErrors.NotFound(code);
        }

        PermitGroupErrors.RequireRowVersion(group, request.RowVersion);

        var members = await PermitGroupAssembler.MembersAsync(request.TypeCodes, permits, ct).ConfigureAwait(false);
        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var before = PermitGroupAssembler.MembersJson(group, types);
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var keep = members.Select(m => m.Id).ToList();
            groups.RemoveMembersNotIn(group, keep);
            group.ReplaceMembers(keep, userId, now);

            // Ревізія реєстру групи - сигнал іншим вузлам перечитати знімок складу.
            if (await registries.FindDefinitionByIdAsync(group.RegistryDefId, innerCt).ConfigureAwait(false) is { } registry)
            {
                registry.BumpDataRevision();
            }

            try
            {
                await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.CellConflict)
            {
                throw PermitGroupErrors.Changed(group.Code, group.RowVersion);
            }

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: now,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitGroup",
                    EntityId: group.Id,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "ReplacePermitGroupTypes",
                    OldJson: before,
                    NewJson: PermitGroupAssembler.MembersJson(group, types),
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        cache.Invalidate();

        var usage = await registries.CountTemplateUsageAsync(ct).ConfigureAwait(false);
        return PermitGroupAssembler.ToDto(group, types, usage);
    }
}

/// <summary>
/// Видаляє групу, яку не використовує жодна колонка/поле шаблонів, разом зі складом і прихованим реєстром.
/// Право <c>Permit.EditType</c>. ⛔ Використану групу видаляти не можна - лише деактивувати (<c>ECR-PRM-4098</c>).
/// </summary>
public sealed class DeletePermitGroupHandler(
    IPermitGroupStore groups,
    IPermitTypeStore permits,
    IRegistryStore registries,
    IUnitOfWork uow,
    IAuditWriter audit,
    PermitGroupSnapshotCache cache,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на зміну типів дозволів (і їхніх груп).</summary>
    public const string Permission = "Permit.EditType";

    /// <summary>Видаляє групу.</summary>
    /// <param name="code">Код групи.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0405</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-4098</c>.</exception>
    public async Task HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        var group = await groups.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (group is null || RegistryAccess.IsDenied(profile, group.RegistryDefId))
        {
            throw PermitGroupErrors.NotFound(code);
        }

        var usage = await registries.CountTemplateUsageAsync(ct).ConfigureAwait(false);
        if (usage.GetValueOrDefault(group.RegistryDefId) is { } used && (used.Columns > 0 || used.Templates > 0))
        {
            throw PermitGroupErrors.InUse(group.Code, used.Columns, used.Templates);
        }

        var types = (await permits.ListSummariesAsync(ct).ConfigureAwait(false)).ToDictionary(t => t.Id);
        var before = PermitGroupAssembler.MembersJson(group, types);
        var groupId = group.Id;
        var now = clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await groups.RemoveAsync(group, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: now,
                    TemplateVersionId: 0,
                    EntityType: "cfg.PermitGroup",
                    EntityId: groupId,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: "DeletePermitGroup",
                    OldJson: before,
                    NewJson: null,
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        cache.Invalidate();
    }
}
