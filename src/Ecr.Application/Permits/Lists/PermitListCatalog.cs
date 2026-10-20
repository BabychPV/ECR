// src/Ecr.Application/Permits/Lists/PermitListCatalog.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits.Lists;

/// <summary>Список вибору разом з тим, що потрібно для запису в нього (усередині Application).</summary>
/// <param name="Dto">Опис для відповіді.</param>
/// <param name="Scope">Область.</param>
/// <param name="RegistryDefId">Реєстр зі значеннями; для таблиці дозволу <c>null</c>.</param>
/// <param name="Usages">Стовпці, що беруть значення (для <c>TypeOnly</c> - рівно один).</param>
internal sealed record PermitListEntry(
    PermitListDto Dto, PermitListScope Scope, int? RegistryDefId, IReadOnlyList<PermitListUsage> Usages);

/// <summary>
/// Збирання переліку списків із проєкції стовпців (Permits, PM-08e): два запити на весь перелік - не залежно від числа
/// типів (раніше клієнт читав визначення кожного типу окремо).
/// </summary>
internal static class PermitListCatalog
{
    internal const string Shared = "shared";
    internal const string TypeOnly = "type";
    internal const string SamePermit = "permit";

    /// <summary>Право на читання довідників; типи дозволів - надбудова над ними.</summary>
    internal const string ViewPermission = "Registry.View";

    /// <summary>Право на зміну значень спільного довідника.</summary>
    internal const string EditSharedPermission = "Registry.EditData";

    /// <summary>Право на зміну значень списку «лише для типу».</summary>
    internal const string EditTypePermission = "Permit.EditType";

    internal static string SharedKey(string registryCode) => $"{Shared}:{registryCode}";

    internal static string ColumnKey(string prefix, PermitListUsage use) => $"{prefix}:{use.TypeCode}:{use.TableCode}:{use.FieldCode}";

    /// <summary>Перелік видимих читачеві списків: спільні - за довідником, решта - за стовпцем.</summary>
    /// <param name="lists">Сховище проєкції.</param>
    /// <param name="profile">Профіль читача.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<IReadOnlyList<PermitListEntry>> BuildAsync(
        IPermitListStore lists, Security.AccessProfile profile, CancellationToken ct)
    {
        var usages = (await lists.ListUsagesAsync(ct).ConfigureAwait(false))
            .Where(u => !RegistryAccess.IsDenied(profile, u.HeaderRegistryDefId))
            .Where(u => u.ListRegistryDefId is not { } id || !RegistryAccess.IsDenied(profile, id))
            .ToList();

        var counts = await lists
            .CountActiveEntriesAsync([.. usages.Where(u => u.ListRegistryDefId is not null).Select(u => u.ListRegistryDefId!.Value).Distinct()], ct)
            .ConfigureAwait(false);

        var canEditType = Security.PermissionCheck.IsGranted(profile, EditTypePermission);
        var result = new List<PermitListEntry>();

        foreach (var group in usages.Where(u => u.Scope == PermitListScope.SharedRegistry).GroupBy(u => u.ListRegistryDefId!.Value))
        {
            var first = group.First();
            result.Add(new PermitListEntry(
                new PermitListDto(
                    SharedKey(first.ListRegistryCode!),
                    nameof(PermitListScope.SharedRegistry),
                    first.ListRegistryCode,
                    null,
                    Copy(first.ListRegistryName!),
                    counts.GetValueOrDefault(group.Key),
                    RegistryAccess.CanAccess(profile, EditSharedPermission, GrantLevel.Write, group.Key),
                    [.. group.Select(Use)]),
                PermitListScope.SharedRegistry,
                group.Key,
                [.. group]));
        }

        foreach (var use in usages.Where(u => u.Scope == PermitListScope.TypeOnly))
        {
            result.Add(new PermitListEntry(
                new PermitListDto(
                    ColumnKey(TypeOnly, use),
                    nameof(PermitListScope.TypeOnly),
                    use.ListRegistryCode,
                    use.TypeCode,
                    Copy(use.FieldName),
                    counts.GetValueOrDefault(use.ListRegistryDefId!.Value),
                    canEditType,
                    [Use(use)]),
                PermitListScope.TypeOnly,
                use.ListRegistryDefId,
                [use]));
        }

        foreach (var use in usages.Where(u => u.Scope == PermitListScope.SamePermitTable))
        {
            result.Add(new PermitListEntry(
                new PermitListDto(
                    ColumnKey(SamePermit, use),
                    nameof(PermitListScope.SamePermitTable),
                    null,
                    use.TypeCode,
                    Copy(use.FieldName),
                    null,
                    false,
                    [Use(use)]),
                PermitListScope.SamePermitTable,
                null,
                [use]));
        }

        return result;
    }

    /// <summary>Чинні значення списку: активні, не видалені; порядок показу - за позицією.</summary>
    internal static async Task<IReadOnlyList<PermitListItemDto>> ItemsAsync(
        IRegistryStore registries, int? registryDefId, CancellationToken ct)
    {
        if (registryDefId is not { } id)
        {
            return [];
        }

        return [.. (await registries.ListEntriesAsync(id, ct).ConfigureAwait(false))
            .Where(e => e.IsActive && !e.IsDeleted)
            .OrderBy(e => e.Ordinal).ThenBy(e => e.Id)
            .Select(e => new PermitListItemDto(e.Id, e.Code, new Dictionary<string, string>(e.DisplayL10n.Values, StringComparer.OrdinalIgnoreCase)))];
    }

    /// <summary>Список за ключем або <c>404 ECR-PRM-0404 list</c> (невидимий читачеві - те саме).</summary>
    internal static PermitListEntry Find(IReadOnlyList<PermitListEntry> entries, string key)
        => entries.FirstOrDefault(e => string.Equals(e.Dto.Key, key, StringComparison.Ordinal)) ?? throw NotFound(key);

    internal static NotFoundException NotFound(string key)
        => new(
            ErrorCodes.PermitTypeNotFound,
            $"Списку «{key}» не існує.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0404.list", ["key"] = key });

    private static PermitListUseDto Use(PermitListUsage use)
        => new(use.TypeCode, Copy(use.TypeName), use.TableCode, Copy(use.TableName), use.FieldCode, Copy(use.FieldName));

    private static Dictionary<string, string> Copy(Ecr.Domain.ValueObjects.LocalizedText text)
        => new(text.Values, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Перелік списків вибору дозволів. Право <c>Registry.View</c>.</summary>
public sealed class ListPermitListsHandler(
    IPermitListStore lists,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Читає перелік: ОДИН запит проєкції стовпців і один підрахунок значень.</summary>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<PermitListDto>> HandleAsync(CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, PermitListCatalog.ViewPermission, ct).ConfigureAwait(false);

        return [.. (await PermitListCatalog.BuildAsync(lists, profile, ct).ConfigureAwait(false)).Select(e => e.Dto)];
    }
}

/// <summary>Список зі значеннями. Право <c>Registry.View</c>.</summary>
public sealed class GetPermitListHandler(
    IPermitListStore lists,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Читає список за ключем.</summary>
    /// <param name="key">Ключ зі шляху.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>: списку немає або він схований забороною.</exception>
    public async Task<PermitListDetailDto> HandleAsync(string key, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, PermitListCatalog.ViewPermission, ct).ConfigureAwait(false);
        var entry = PermitListCatalog.Find(await PermitListCatalog.BuildAsync(lists, profile, ct).ConfigureAwait(false), key);

        return new PermitListDetailDto(
            entry.Dto, await PermitListCatalog.ItemsAsync(registries, entry.RegistryDefId, ct).ConfigureAwait(false));
    }
}
