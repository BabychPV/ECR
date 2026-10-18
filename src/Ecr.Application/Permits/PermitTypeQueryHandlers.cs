// src/Ecr.Application/Permits/PermitTypeQueryHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits;

/// <summary>Збирання відповіді з типу і його складу (спільне для читання й змін).</summary>
internal static class PermitTypeAssembler
{
    /// <summary>Повне визначення: чернетка, якщо вона є, інакше опубліковане.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="content">Показаний склад.</param>
    internal static PermitTypeDto ToDto(PermitTypeDef type, PermitTypeContent content)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(content);

        // Версія чернетки: перша для ніколи не опублікованого типу, інакше наступна за опублікованою.
        int? draftVersion = type.HasDraft
            ? (type.Status == PermitTypeStatus.Published ? type.Version + 1 : type.Version)
            : null;

        return new PermitTypeDto(
            type.Code,
            content.NameL10n,
            content.Category,
            PermitTypeCodec.StatusName(type.Status),
            type.Version,
            draftVersion,
            type.HasDraft,
            Convert.ToBase64String(type.RowVersion),
            PermitHeaderFields.All,
            content.Tables,
            content.Rules,
            type.PublishedAt,
            type.UpdatedAt);
    }

    /// <summary>Склад, який бачить читач: чернетка або опублікований.</summary>
    /// <param name="type">Тип.</param>
    /// <param name="reader">Читач опублікованого складу.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<PermitTypeContent> ShownAsync(PermitTypeDef type, PermitTypeReader reader, CancellationToken ct)
        => type.HasDraft
            ? PermitTypeCodec.Parse(type.DraftJson!)
            : await reader.ReadPublishedAsync(type, ct).ConfigureAwait(false);
}

/// <summary>Перелік типів дозволів. Право <c>Registry.View</c>.</summary>
public sealed class ListPermitTypesHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (типи — надбудова над ними).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає сторінку переліку.</summary>
    /// <param name="page">Розмір сторінки (1..500) і курсор — код останнього типу попередньої сторінки.</param>
    /// <param name="category">Фільтр за категорією.</param>
    /// <param name="status"><c>Draft</c> чи <c>Published</c>.</param>
    /// <param name="q">Підрядок коду чи назви.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<PermitTypeListItemDto>> HandleAsync(
        CursorRequest page, string? category, string? status, string? q, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // Типів дозволів десятки: метадані читаються цілком, а сторінка — за кодом (стійкий порядок без OFFSET).
        var types = (await permits.ListAsync(ct).ConfigureAwait(false))
            .Where(t => !Registries.RegistryAccess.IsDenied(profile, t.HeaderRegistryDefId))
            .Where(t => string.IsNullOrWhiteSpace(category)
                        || string.Equals(PermitTypeCodec.CategoryName(t.Category), category, StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(status)
                        || string.Equals(PermitTypeCodec.StatusName(t.Status), status, StringComparison.Ordinal))
            .Where(t => string.IsNullOrWhiteSpace(q)
                        || t.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || t.NameL10n.Values.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Where(t => page.Cursor is null || string.CompareOrdinal(t.Code, page.Cursor) > 0)
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .Take(page.Limit + 1)
            .ToList();

        var hasMore = types.Count > page.Limit;
        var shown = types.Take(page.Limit).ToList();

        var allTables = (await permits.ListAllTablesAsync(ct).ConfigureAwait(false))
            .ToLookup(t => t.PermitTypeDefId);
        var definitions = (await registries.ListDefinitionsAsync(ct).ConfigureAwait(false)).ToDictionary(d => d.Id);
        var counts = await permits.CountPermitsAsync([.. shown.Select(t => t.HeaderRegistryDefId)], ct).ConfigureAwait(false);
        var names = await registries
            .GetUserDisplayNamesAsync([.. shown.Where(t => t.UpdatedByUserId is not null).Select(t => t.UpdatedByUserId!.Value).Distinct()], ct)
            .ConfigureAwait(false);

        var items = shown.Select(type => new PermitTypeListItemDto(
            type.Code,
            PermitTypeReader.Copy(type.NameL10n.Values),
            PermitTypeCodec.CategoryName(type.Category),
            PermitTypeCodec.StatusName(type.Status),
            type.Version,
            type.HasDraft,
            Tables(type, allTables[type.Id], definitions),
            counts.GetValueOrDefault(type.HeaderRegistryDefId),
            type.UpdatedAt,
            type.UpdatedByUserId is { } id ? names.GetValueOrDefault(id) : null)).ToList();

        return new PagedResult<PermitTypeListItemDto>(items, hasMore ? shown[^1].Code : null, null);
    }

    private static List<PermitTypeTableSummaryDto> Tables(
        PermitTypeDef type, IEnumerable<PermitTypeTable> published, Dictionary<int, RegistryDef> definitions)
    {
        // Чернетка показує склад, який людина збирає; без неї — опублікований (лічильник стовпців з реєстру).
        if (type.HasDraft)
        {
            return [.. PermitTypeCodec.Parse(type.DraftJson!).Tables
                .OrderBy(t => t.Order)
                .Select(t => new PermitTypeTableSummaryDto(t.Code, t.NameL10n, t.Columns.Count))];
        }

        return [.. published.Select(t => new PermitTypeTableSummaryDto(
            t.Code,
            PermitTypeReader.Copy(t.NameL10n.Values),
            definitions.TryGetValue(t.RegistryDefId, out var def)
                ? def.Fields.Count(f => f.RelationKind != RegistryRelationKind.Composition)
                : 0))];
    }
}

/// <summary>Повне визначення типу (чернетка, якщо є). Право <c>Registry.View</c>.</summary>
public sealed class GetPermitTypeHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає визначення.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Типу немає (або на його шапку заборона читачу) — <c>ECR-PRM-0404</c>.</exception>
    public async Task<PermitTypeDto> HandleAsync(string code, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        var content = await PermitTypeAssembler
            .ShownAsync(type, new PermitTypeReader(registries, permits), ct).ConfigureAwait(false);

        return PermitTypeAssembler.ToDto(type, content);
    }
}
