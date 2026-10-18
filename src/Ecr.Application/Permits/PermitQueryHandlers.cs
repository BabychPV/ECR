// src/Ecr.Application/Permits/PermitQueryHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Permits;

/// <summary>Один дозвіл: шапка, стан, таблиці з підсумками. Право <c>Registry.View</c> (або грант <c>Read</c>) на реєстр шапки.</summary>
public sealed class GetPermitHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає дозвіл.</summary>
    /// <param name="permitId">Ідентифікатор дозволу.</param>
    /// <param name="asOf">Дата, на яку рахується стан; <c>null</c> - сьогодні (UTC).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    public async Task<PermitDto> HandleAsync(long permitId, DateOnly? asOf, CancellationToken ct)
    {
        var (type, entry) = await new PermitLocator(permits, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Read, ct).ConfigureAwait(false);

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var view = await reader.ViewAsync(type, ct).ConfigureAwait(false) ?? throw PermitErrors.NotFound(permitId);
        var headers = await reader.HeadersAsync(view, [entry.Id], ct).ConfigureAwait(false);
        var header = headers.Count > 0 ? headers[0] : throw PermitErrors.NotFound(permitId);

        return await reader.DtoAsync(view, header, asOf ?? reader.Today, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Перелік дозволів усіх типів, доступних читачу (Permits, PM-04). Права - як у <see cref="GetPermitHandler"/>.
/// </summary>
/// <remarks>
/// ⚠ Фільтри й сторінки - у пам'яті над шапками типів: дозволів сотні, а підсумки екрана (активні / закінчуються за 120
/// днів / ніким не читаються) клієнт рахує з повного переліку (<c>limit</c> до 500). Курсор - <c>Id</c> дозволу.
/// </remarks>
public sealed class ListPermitsHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryRowsQuery rows,
    IPermitStore stats,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на читання довідників.</summary>
    public const string Permission = "Registry.View";

    /// <summary>Читає сторінку переліку.</summary>
    /// <param name="page">Розмір сторінки (1..500) і курсор - Id останнього дозволу попередньої сторінки.</param>
    /// <param name="typeCode">Лише цей тип.</param>
    /// <param name="status"><c>Draft</c>, <c>Active</c> або <c>Expired</c> на <paramref name="asOf"/>.</param>
    /// <param name="q">Підрядок номера, розташування чи назви типу.</param>
    /// <param name="asOf">Дата, на яку рахується стан; <c>null</c> - сьогодні (UTC).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PagedResult<PermitListItemDto>> HandleAsync(
        CursorRequest page, string? typeCode, string? status, string? q, DateOnly? asOf, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        if (!page.IsValid)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір сторінки поза межами 1..{CursorRequest.MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = CursorRequest.MaxLimit.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (!string.IsNullOrWhiteSpace(status) && status is not (PermitStatuses.Draft or PermitStatuses.Active or PermitStatuses.Expired))
        {
            throw PermitErrors.StatusUnknown(status);
        }

        var cursor = 0L;
        if (page.Cursor is not null && !long.TryParse(page.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out cursor))
        {
            throw PermitErrors.CursorInvalid();
        }

        var types = (await permits.ListAsync(ct).ConfigureAwait(false))
            .Where(t => RegistryAccess.CanAccess(profile, Permission, GrantLevel.Read, t.HeaderRegistryDefId))
            .ToList();
        if (types.Count == 0 && !Security.PermissionCheck.IsGranted(profile, Permission))
        {
            throw RegistryAccess.Denied(Permission);
        }

        var reader = new PermitReader(permits, registries, rows, stats, clock);
        var day = asOf ?? reader.Today;
        var items = new List<PermitListItemDto>();
        foreach (var type in types.Where(t => string.IsNullOrWhiteSpace(typeCode) || string.Equals(t.Code, typeCode, StringComparison.Ordinal)))
        {
            if (await reader.ViewAsync(type, ct).ConfigureAwait(false) is { } view)
            {
                items.AddRange(await reader.ListItemsAsync(view, day, ct).ConfigureAwait(false));
            }
        }

        var matched = items
            .Where(i => string.IsNullOrWhiteSpace(status) || i.Status == status)
            .Where(i => string.IsNullOrWhiteSpace(q)
                        || i.Number.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (i.Location?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                        || i.TypeCode.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || i.TypeNameL10n.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Id)
            .ToList();

        var shown = matched.Where(i => i.Id > cursor).Take(page.Limit + 1).ToList();
        var hasMore = shown.Count > page.Limit;
        if (hasMore)
        {
            shown.RemoveAt(shown.Count - 1);
        }

        return new PagedResult<PermitListItemDto>(
            shown, hasMore ? shown[^1].Id.ToString(CultureInfo.InvariantCulture) : null, matched.Count);
    }
}
