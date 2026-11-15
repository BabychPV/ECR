// src/Ecr.Application/Permits/GetPermitUsagesHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// «Де використано» дозвіл (<c>GET /permits/{id}/usages</c>): документи, період, аркуш-таблиця-рядок або поле шапки,
/// де дозвіл стоїть значенням.
/// </summary>
/// <remarks>
/// <para>
/// Права - як у читання дозволу (<c>Registry.View</c> + грант <c>Read</c> на реєстр шапки типу; заборона - той самий
/// <c>404</c>). Додатково відповідь звужена проєктами, документи яких читач бачить (<c>Document.View</c> у проєкті):
/// чужі документи не рахуються й не називаються, інакше картка дозволу розкривала б існування документів.
/// </para>
/// <para>
/// ⚠ Прихований реєстр шапки типу дозволу (FEATURE-PERMITS R4) тут НЕ виключається - це відповідь на питання «де
/// обрано саме цей дозвіл», а не перелік довідників.
/// </para>
/// </remarks>
public sealed class GetPermitUsagesHandler(
    IPermitTypeStore permitTypes,
    IRegistryStore registries,
    IPermitStore permits,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на читання довідників (дозволів).</summary>
    public const string Permission = "Registry.View";

    /// <summary>Право на перегляд документів проєкту: без нього документ у відповідь не потрапляє.</summary>
    public const string DocumentPermission = "Document.View";

    /// <summary>Розмір переліку за замовчуванням.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Найбільший розмір переліку.</summary>
    public const int MaxLimit = 500;

    /// <summary>Читає використання.</summary>
    /// <param name="permitId">Дозвіл (запис шапки).</param>
    /// <param name="periodKey">Лише цей період; <c>null</c> - усі.</param>
    /// <param name="limit">Розмір переліку 1..500; <c>null</c> - 200. Лічильники рахують усе.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="BusinessRuleException">Період чи ліміт поза межами - <c>ECR-REQ-0422</c>.</exception>
    public async Task<PermitUsageDto> HandleAsync(long permitId, int? periodKey, int? limit, CancellationToken ct)
    {
        await new PermitLocator(permitTypes, registries, access, currentUser)
            .FindAsync(permitId, Permission, GrantLevel.Read, ct).ConfigureAwait(false);

        if (periodKey is { } key && !new PeriodKey(key).IsValid)
        {
            throw Invalid("err.ECR-REQ-0422.malformedRequest", "Період задано хибно (очікується YYYYNN).");
        }

        if (limit is { } l && l is < 1 or > MaxLimit)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Розмір переліку поза межами 1..{MaxLimit}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.pageSizeOutOfRange",
                    ["max"] = MaxLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var profile = await access.BuildProfileAsync(currentUser.UserId!.Value, ct).ConfigureAwait(false);
        var projects = ListDocumentsHandler.ReadableProjects(profile, DocumentPermission);
        if (projects.Count == 0)
        {
            return new PermitUsageDto(permitId, 0, 0, false, []);
        }

        var page = await permits.FindUsagesAsync(permitId, projects, periodKey, limit ?? DefaultLimit, ct).ConfigureAwait(false);
        return await WithoutHiddenCellsAsync(profile, page, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ⛔ S-4: місце комірки поза межами читання документа (Deny, D-214, період) не називається — те саме рішення, що й зріз.
    /// Поля шапки лишаються: шапку бачить кожен, хто бачить документ.
    /// ⛔ B128c: запис історії рядка (<c>history</c> з таблицею) — те саме правило, що й комірка: колонка в межах читання хоч
    /// в одному з періодів, які дає сховище (<see cref="PermitUsagePage.ItemPeriods"/>): частина місяця — її місяць; сегмент
    /// рядка — періоди, де рядок живий І сегмент чинний (з фільтром — лише період фільтра). Без таких періодів запис не
    /// називається. Запис історії поля шапки лишається, як шапка.
    /// ⚠ F13: НЕ місяць <c>ValidFrom</c> — він і пропускав запис рядка, живого лише в недоступному періоді, і ховав сегмент,
    /// чинний і в доступному.
    /// </summary>
    private async Task<PermitUsageDto> WithoutHiddenCellsAsync(
        Security.AccessProfile profile, PermitUsagePage page, CancellationToken ct)
    {
        var usage = page.Usage;
        var scopes = new Dictionary<long, Security.DocumentReadScope>();
        var kept = new List<PermitUsageItemDto>(usage.Items.Count);
        var removedHistory = 0;

        for (var i = 0; i < usage.Items.Count; i++)
        {
            var item = usage.Items[i];
            var isCell = string.Equals(item.Kind, PermitUsageKinds.Cell, StringComparison.Ordinal);
            var isRowHistory = string.Equals(item.Kind, PermitUsageKinds.History, StringComparison.Ordinal) && item.TableCode is not null;
            if (!isCell && !isRowHistory)
            {
                kept.Add(item);
                continue;
            }

            if (!scopes.TryGetValue(item.DocumentId, out var scope))
            {
                scope = await access.ReadScopeAsync(profile, item.DocumentId, ct).ConfigureAwait(false);
                scopes[item.DocumentId] = scope;
            }

            var periods = page.ItemPeriods[i] ?? [];
            if (item.TableCode is { } table
                && periods.Any(p => scope.CanReadColumnCodeIn(table, item.FieldCode, new PeriodKey(p))))
            {
                kept.Add(item);
            }
            else if (isRowHistory)
            {
                removedHistory++;
            }
        }

        var removed = usage.Items.Count - kept.Count;
        return removed == 0
            ? usage
            : usage with
            {
                LinkCount = Math.Max(0, usage.LinkCount - (removed - removedHistory)),
                HistoryLinkCount = Math.Max(0, usage.HistoryLinkCount - removedHistory),
                Items = kept,
            };
    }

    private static BusinessRuleException Invalid(string messageKey, string message)
        => new(ErrorCodes.RequestInvalid, message, new Dictionary<string, object?> { ["messageKey"] = messageKey });
}
