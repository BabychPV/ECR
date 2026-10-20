// src/Ecr.Application/Permits/Lists/ChangePermitListItemsHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits.Lists;

/// <summary>
/// Додає, змінює й прибирає значення списку вибору дозволу: усе або нічого (Permits, PM-08e).
/// </summary>
/// <remarks>
/// <para>
/// Права. Спільний довідник - <c>Registry.EditData</c> АБО грант <c>Write</c> на нього (<c>RegistryAccess</c>), і запис іде
/// через ТІ САМІ обробники, що й сторінка «Довідники» (<see cref="UpsertRegistryEntryHandler"/>,
/// <see cref="DeleteRegistryEntryHandler"/>): ключі, правила, ревізія, аудит і заборона на посилання лишаються їхніми.
/// Список «лише для типу» - <c>Permit.EditType</c>; його реєстр прихований від переліку «Довідники», тож іншого шляху до
/// нього немає. Таблиця того самого дозволу - значення це рядки дозволу, а не список: <c>422 listReadOnly</c>.
/// </para>
/// <para>
/// ⚠ «Лише для типу»: істина про опублікований тип - реєстр списку, але відкрита чернетка нової версії несе власні
/// значення, і публікація ВИМИКАЄ ті, яких у чернетці немає. Тому зміна лягає й у чернетку (<c>Choices</c> стовпця) - інакше
/// додане значення зникло б при наступній публікації. Токен <c>rowVersion</c> типу при цьому змінюється: відкритий
/// конструктор побачить <c>409 changed</c> і перечитає.
/// </para>
/// <para>
/// ⚠ Видалення значення «лише для типу» - вимкнення (<c>IsActive = false</c>), як і при публікації: записи дозволів зберігають
/// посилання. Останнє значення прибрати не можна (<c>typeOnlyNoChoices</c>).
/// </para>
/// </remarks>
public sealed class ChangePermitListItemsHandler(
    IPermitListStore lists,
    IPermitTypeStore permits,
    IRegistryStore registries,
    UpsertRegistryEntryHandler upsert,
    DeleteRegistryEntryHandler delete,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Скільки змін приймає один запит.</summary>
    public const int MaxChanges = 200;

    private const string Add = "Add";
    private const string Update = "Update";
    private const string Remove = "Remove";
    private const string Operation = "ChangePermitListItems";
    private const string AuditReason = "Зміна значень списку вибору дозволу";

    /// <summary>Застосовує зміни й повертає список у новому стані.</summary>
    /// <param name="key">Ключ списку зі шляху.</param>
    /// <param name="request">Зміни.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>: списку немає або він схований забороною.</exception>
    /// <exception cref="AccessDeniedException">Бракує <c>Registry.EditData</c> (грантa) або <c>Permit.EditType</c>.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c>: таблиця дозволу, порожні чи повторені значення, невідома дія.</exception>
    public async Task<PermitListDetailDto> HandleAsync(string key, ChangePermitListItemsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Потрібна автентифікація.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var entry = PermitListCatalog.Find(await PermitListCatalog.BuildAsync(lists, profile, ct).ConfigureAwait(false), key);

        if (entry.Scope == PermitListScope.SamePermitTable)
        {
            throw ListReadOnly(key);
        }

        // Право - ДО розбору змін: читач без права не дізнається з відповіді, що саме не так із тілом запиту.
        if (entry.Scope == PermitListScope.SharedRegistry)
        {
            await RegistryAccess
                .RequireAsync(access, currentUser, PermitListCatalog.EditSharedPermission, GrantLevel.Write, entry.RegistryDefId!.Value, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await Security.PermissionCheck.RequireAsync(access, currentUser, PermitListCatalog.EditTypePermission, ct).ConfigureAwait(false);
        }

        RequireChanges(request);

        if (entry.Scope == PermitListScope.SharedRegistry)
        {
            await uow.ExecuteInTransactionAsync(token => ChangeSharedAsync(entry, request.Changes, token), ct).ConfigureAwait(false);
        }
        else
        {
            await uow.ExecuteInTransactionAsync(token => ChangeTypeOnlyAsync(entry, request.Changes, userId, token), ct).ConfigureAwait(false);
        }

        var items = await PermitListCatalog.ItemsAsync(registries, entry.RegistryDefId, ct).ConfigureAwait(false);

        return new PermitListDetailDto(entry.Dto with { ItemCount = items.Count }, items);
    }

    private static void RequireChanges(ChangePermitListItemsRequest request)
    {
        if (request.Changes is null || request.Changes.Count == 0 || request.Changes.Count > MaxChanges)
        {
            throw Invalid("Змін має бути від 1 до " + MaxChanges.ToString(CultureInfo.InvariantCulture) + ".", "action", string.Empty);
        }

        foreach (var change in request.Changes)
        {
            if (change is null
                || (change.Action != Add && change.Action != Update && change.Action != Remove)
                || (change.Action == Add && (change.Id is not null || change.DisplayL10n is null))
                || (change.Action == Update && (change.Id is null || change.DisplayL10n is null))
                || (change.Action == Remove && change.Id is null))
            {
                throw Invalid("Зміна списку задана неповно або невідомою дією.", "action", change?.Action ?? string.Empty);
            }
        }
    }

    // ───────── Спільний довідник: ті самі обробники, що й сторінка «Довідники» ─────────

    private async Task ChangeSharedAsync(PermitListEntry entry, IReadOnlyList<PermitListItemChange> changes, CancellationToken ct)
    {
        var registryId = entry.RegistryDefId!.Value;
        var definition = await registries.FindDefinitionByIdAsync(registryId, ct).ConfigureAwait(false)
            ?? throw PermitListCatalog.NotFound(entry.Dto.Key);
        var nextNumber = NextNumber(await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false));

        foreach (var change in changes)
        {
            switch (change.Action)
            {
                case Add:
                    var code = definition.CodeMode == RegistryCodeMode.Auto
                        ? string.Empty
                        : "V" + (nextNumber++).ToString("D3", CultureInfo.InvariantCulture);
                    await upsert.HandleWithWarningsAsync(
                        new RegistryEntryUpsertDto(null, registryId, code, Display(change, requireEnglish: false, entry.Dto.Key), null, new Dictionary<string, object?>()),
                        definition.Code, ct).ConfigureAwait(false);
                    break;

                case Update:
                    var existing = await RequireEntryAsync(registryId, definition.Code, change.Id!.Value, ct).ConfigureAwait(false);
                    await upsert.HandleWithWarningsAsync(
                        new RegistryEntryUpsertDto(existing.Id, registryId, existing.Code, Display(change, requireEnglish: false, entry.Dto.Key), existing.ParentEntryId, new Dictionary<string, object?>()),
                        definition.Code, ct).ConfigureAwait(false);
                    break;

                default:
                    await delete.HandleAsync(definition.Code, change.Id!.Value, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    // ───────── «Лише для типу»: прихований реєстр + чернетка типу ─────────

    private async Task ChangeTypeOnlyAsync(
        PermitListEntry entry, IReadOnlyList<PermitListItemChange> changes, int userId, CancellationToken ct)
    {
        var registryId = entry.RegistryDefId!.Value;
        var use = entry.Usages[0];
        ExternalRegistryGuard.EnsureManualEditAllowed(
            await registries.FindDefinitionByIdAsync(registryId, ct).ConfigureAwait(false) ?? throw PermitListCatalog.NotFound(entry.Dto.Key));
        var entries = (await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false)).ToList();
        var nextNumber = NextNumber(entries);
        var ordinal = entries.Select(e => e.Ordinal).DefaultIfEmpty(0).Max();
        var live = entries.Where(e => e.IsActive && !e.IsDeleted).ToList();
        var draft = new List<(string Action, string? Old, string? New)>();

        foreach (var change in changes)
        {
            if (change.Action == Add)
            {
                var display = Display(change, requireEnglish: true, use.FieldCode);
                var text = display.Get("en")!;
                RequireUnique(live, text, ignore: null, use.FieldCode);
                var created = new RegistryEntry(
                    registryId, EcrCode.Create("V" + (nextNumber++).ToString("D3", CultureInfo.InvariantCulture)), display, userId, clock.UtcNow);
                created.SetOrdinal(++ordinal);
                registries.Add(created);
                live.Add(created);
                draft.Add((Add, null, text));
                continue;
            }

            var tracked = await registries.FindEntryAsync(change.Id!.Value, ct).ConfigureAwait(false);
            if (tracked is null || tracked.IsDeleted || tracked.RegistryDefId != registryId || !tracked.IsActive)
            {
                throw RegistryAccess.EntryNotFound(change.Id.Value, entry.Dto.RegistryCode ?? entry.Dto.Key);
            }

            var oldText = tracked.DisplayL10n.Get("en") ?? tracked.Code;
            if (change.Action == Update)
            {
                var display = Display(change, requireEnglish: true, use.FieldCode);
                var text = display.Get("en")!;
                RequireUnique(live, text, ignore: tracked, use.FieldCode);
                tracked.Rename(display);
                draft.Add((Update, oldText, text));
            }
            else
            {
                if (live.Count <= 1)
                {
                    throw Invalid("Останнє значення списку «лише для типу» не прибирається.", "typeOnlyNoChoices", use.FieldCode);
                }

                tracked.Deactivate();
                live.Remove(tracked);
                draft.Add((Remove, oldText, null));
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        await PatchDraftAsync(use, draft, userId, ct).ConfigureAwait(false);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.PermitTypeDef",
                EntityId: use.TypeId,
                ChangeClass: ChangeClass.Safe,
                Operation: Operation,
                OldJson: null,
                NewJson: JsonSerializer.Serialize(new { list = entry.Dto.Key, changes = draft.Select(d => new { d.Action, d.Old, d.New }) }),
                ChangeReason: AuditReason,
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }

    /// <summary>Переносить ті самі зміни у відкриту чернетку типу (якщо вона є) - див. примітку класу.</summary>
    private async Task PatchDraftAsync(
        PermitListUsage use, IReadOnlyList<(string Action, string? Old, string? New)> draft, int userId, CancellationToken ct)
    {
        var type = await permits.FindByCodeAsync(use.TypeCode, ct).ConfigureAwait(false);
        if (type is null || !type.HasDraft || PermitTypeCodec.IsOversized(type.DraftJson))
        {
            return;
        }

        var content = PermitTypeCodec.Parse(type.DraftJson!);
        var changed = false;
        var tables = content.Tables.Select(table =>
        {
            if (!string.Equals(table.Code, use.TableCode, StringComparison.OrdinalIgnoreCase))
            {
                return table;
            }

            return table with
            {
                Columns = [.. table.Columns.Select(column =>
                {
                    if (!string.Equals(column.Code, use.FieldCode, StringComparison.OrdinalIgnoreCase)
                        || column.ListScope != nameof(PermitListScope.TypeOnly))
                    {
                        return column;
                    }

                    changed = true;
                    return column with { Choices = Merge(column.Choices ?? [], draft) };
                })],
            };
        }).ToList();

        if (!changed)
        {
            return;
        }

        var json = PermitTypeCodec.Serialize(content with { Tables = tables });
        if (PermitTypeCodec.IsOversized(json))
        {
            throw PermitTypeErrors.DraftTooLarge();
        }

        type.SaveDraft(json, userId, clock.UtcNow);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static List<string> Merge(IReadOnlyList<string> choices, IReadOnlyList<(string Action, string? Old, string? New)> draft)
    {
        var result = choices.ToList();
        foreach (var (action, old, next) in draft)
        {
            var at = old is null ? -1 : result.FindIndex(c => string.Equals(c, old, StringComparison.OrdinalIgnoreCase));
            switch (action)
            {
                case Add when !result.Contains(next!, StringComparer.OrdinalIgnoreCase):
                    result.Add(next!);
                    break;
                case Update when at >= 0:
                    result[at] = next!;
                    break;
                case Remove when at >= 0:
                    result.RemoveAt(at);
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    // ───────── Спільне ─────────

    private async Task<RegistryEntry> RequireEntryAsync(int registryId, string registryCode, long id, CancellationToken ct)
    {
        var entry = await registries.FindEntryAsync(id, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != registryId)
        {
            throw RegistryAccess.EntryNotFound(id, registryCode);
        }

        return entry;
    }

    private static int NextNumber(IEnumerable<RegistryEntry> entries)
        => entries
            .Select(e => e.Code.Length > 1 && e.Code[0] == 'V' && int.TryParse(e.Code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

    private static LocalizedText Display(PermitListItemChange change, bool requireEnglish, string label)
    {
        var values = (change.DisplayL10n ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);

        if (values.Count == 0 || (requireEnglish && !values.ContainsKey("en")))
        {
            throw Invalid("Значення списку порожнє (для списку «лише для типу» потрібна англійська назва).", "display", label);
        }

        if (values.Values.Any(v => v.Length > PermitTypeLimits.MaxNameLength))
        {
            throw new BusinessRuleException(
                ErrorCodes.PermitTypeInvalid,
                $"Значення довше за {PermitTypeLimits.MaxNameLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRM-0422.textTooLong",
                    ["item"] = "display",
                    ["max"] = PermitTypeLimits.MaxNameLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        return new LocalizedText(values);
    }

    private static void RequireUnique(IEnumerable<RegistryEntry> live, string text, RegistryEntry? ignore, string fieldCode)
    {
        if (live.Any(e => !ReferenceEquals(e, ignore) && string.Equals(e.DisplayL10n.Get("en") ?? e.Code, text, StringComparison.OrdinalIgnoreCase)))
        {
            throw new BusinessRuleException(
                ErrorCodes.PermitTypeInvalid,
                $"Значення «{text}» у списку вже є.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.choicesInvalid", ["columnCode"] = fieldCode });
        }
    }

    private static BusinessRuleException ListReadOnly(string key)
        => new(
            ErrorCodes.PermitTypeInvalid,
            $"Список «{key}» - таблиця того самого дозволу: його значення це рядки дозволу.",
            new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.listReadOnly", ["key"] = key });

    private static BusinessRuleException Invalid(string message, string name, string value)
        => name switch
        {
            "typeOnlyNoChoices" => new(
                ErrorCodes.PermitTypeInvalid, message,
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.typeOnlyNoChoices", ["columnCode"] = value }),
            "display" => new(
                ErrorCodes.PermitTypeInvalid, message,
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.choicesInvalid", ["columnCode"] = value }),
            _ => new(
                ErrorCodes.PermitTypeInvalid, message,
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-PRM-0422.listChangeInvalid", ["action"] = value }),
        };
}
