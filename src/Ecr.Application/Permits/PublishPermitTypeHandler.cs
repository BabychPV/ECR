// src/Ecr.Application/Permits/PublishPermitTypeHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Permits;

/// <summary>
/// Публікує чернетку типу АТОМАРНО: шапка, усі таблиці й приховані списки «лише для типу» застосовуються до реєстрів
/// однією транзакцією, разом із надбудовою полів і станом типу (Permits, PM-03). Право <c>Permit.PublishType</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Це НЕ цикл HTTP-викликів публікації реєстрів: <see cref="SaveRegistryDefinitionHandler.ApplyAsync"/> бере
/// ту саму зовнішню транзакцію (<c>IUnitOfWork.ExecuteInTransactionAsync</c> вкладена просто виконується), тож збій
/// на другій таблиці відкочує й шапку, і першу таблицю, і створені реєстри, і рядки типу. Часткового типу не буває.
/// </para>
/// <para>
/// ⚠ Реєстри, на які лише посилаються стовпці (спільні довідники), не змінюються: публікація їх тільки читає.
/// </para>
/// </remarks>
public sealed class PublishPermitTypeHandler(
    IPermitTypeStore permits,
    IRegistryStore registries,
    IRegistryKeyStore keys,
    SaveRegistryDefinitionHandler apply,
    IUnitOfWork uow,
    IAuditWriter audit,
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на публікацію опису довідників.</summary>
    public const string Permission = "Permit.PublishType";

    /// <summary>Публікує чернетку.</summary>
    /// <param name="code">Код типу.</param>
    /// <param name="request">Причина (не коротша за 10 символів) і токен чернетки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException"><c>ECR-PRM-0404</c>.</exception>
    /// <exception cref="ConcurrencyConflictException"><c>ECR-PRM-4093</c>: токен чужий або чернетки немає.</exception>
    /// <exception cref="BusinessRuleException"><c>ECR-PRM-0422</c> (склад, причина), <c>ECR-PRM-4091</c> (код реєстру зайнятий), <c>ECR-REG-0422</c> (ядро реєстрів).</exception>
    public async Task<PermitTypeDto> HandleAsync(string code, PublishPermitTypeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await Security.PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        var userId = SaveRegistryDefinitionHandler.RequireUser(currentUser);

        PermitTypeDefinitionValidator.ValidateReason(request.Reason);

        var type = await permits.FindByCodeAsync(code, ct).ConfigureAwait(false);
        if (type is null || RegistryAccess.IsDenied(profile, type.HeaderRegistryDefId))
        {
            throw PermitTypeErrors.NotFound(code);
        }

        if (!type.HasDraft)
        {
            throw PermitTypeErrors.NoDraft(type.Code);
        }

        PermitTypeErrors.RequireRowVersion(type, request.RowVersion);

        var content = PermitTypeAssembler.Normalize(PermitTypeCodec.Parse(type.DraftJson!));
        var published = type.Status == PermitTypeStatus.Published
            ? await new PermitTypeReader(registries, permits).ReadPublishedAsync(type, ct).ConfigureAwait(false)
            : null;

        PermitTypeDefinitionValidator.Validate(type.Code, content, published);
        PermitTypeDefinitionValidator.ValidateForPublish(content);

        try
        {
            await uow.ExecuteInTransactionAsync(
                innerCt => PublishAsync(type, content, request.Reason.Trim(), userId, profile, innerCt), ct).ConfigureAwait(false);
        }
        catch (ConcurrencyConflictException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.CellConflict)
        {
            throw PermitTypeErrors.Changed(type.Code, type.RowVersion);
        }
        catch (BusinessRuleException ex) when (ex.ErrorCode == Ecr.Domain.Errors.ErrorCodes.PermitTypeDuplicate
                                               && ex.Details?.GetValueOrDefault("concurrent") as string == "true")
        {
            // Паралельна публікація (або заведення) випередила нас на коді реєстру `PRM_…`: для публікації це «тип змінили».
            throw PermitTypeErrors.Changed(type.Code, type.RowVersion);
        }

        return PermitTypeAssembler.ToDto(type, content);
    }

    private async Task PublishAsync(
        PermitTypeDef type, PermitTypeContent content, string reason, int userId, Security.AccessProfile profile, CancellationToken ct)
    {
        var existingTables = (await permits.ListTablesAsync(type.Id, ct).ConfigureAwait(false)).ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);
        var metas = (await permits.ListFieldMetasAsync(type.Id, ct).ConfigureAwait(false)).ToDictionary(m => m.RegistryFieldDefId);

        // 1. Реєстри таблиць, яких ще немає: порожні контейнери, щоб сестринські посилання мали ціль.
        var tableDefs = new Dictionary<string, RegistryDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in content.Tables)
        {
            if (existingTables.TryGetValue(table.Code, out var existing))
            {
                tableDefs[table.Code] = await registries.FindDefinitionByIdAsync(existing.RegistryDefId, ct).ConfigureAwait(false)
                    ?? throw PermitTypeErrors.NotFound(type.Code);
                continue;
            }

            tableDefs[table.Code] = await NewRegistryAsync(
                PermitHeaderFields.TableRegistryCode(type.Code, table.Code), table.NameL10n, isTemporal: false, ct).ConfigureAwait(false);

            // Рядки таблиці дозволу пише клієнт (`entries/batch`) без коду: код видає послідовність (D-157). Режим Manual
            // дав би `422 ECR-CFG-0422 invalidCode` на першому ж рядку (e2e 39279368294). Режим ставиться лише при створенні.
            tableDefs[table.Code].UseCodeMode(RegistryCodeMode.Auto);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var table in content.Tables)
        {
            if (existingTables.TryGetValue(table.Code, out var existing))
            {
                existing.Update(Localized(table.NameL10n), table.Order);
            }
            else
            {
                permits.Add(new PermitTypeTable(type.Id, EcrCode.Create(table.Code), Localized(table.NameL10n), table.Order, tableDefs[table.Code].Id));
            }
        }

        // 2. Шапка: фіксовані поля, ключ за номером, перевірки рівня типу.
        var header = await registries.FindDefinitionByIdAsync(type.HeaderRegistryDefId, ct).ConfigureAwait(false)
            ?? throw PermitTypeErrors.NotFound(type.Code);
        await ApplyHeaderAsync(type, header, content, reason, userId, profile, metas, ct).ConfigureAwait(false);

        // 3. Таблиці зі своїми списками. Порядок не важливий: цілі посилань уже мають ідентифікатори.
        foreach (var table in content.Tables)
        {
            await ApplyTableAsync(type, header, table, tableDefs, reason, userId, profile, metas, ct).ConfigureAwait(false);
        }

        // 4. Стан типу: назва/категорія з чернетки, перша публікація чи нова версія.
        var category = PermitTypeDefinitionValidator.ValidateDetails(content.NameL10n, content.Category);
        var draftJson = type.DraftJson;
        type.ApplyDetails(Localized(content.NameL10n), category);
        if (type.Status == PermitTypeStatus.Draft)
        {
            type.Publish(userId, clock.UtcNow);
        }
        else
        {
            type.Republish(userId, clock.UtcNow);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

        await audit.WriteStructureChangeAsync(
            new StructureChangeRecord(
                ChangedAt: clock.UtcNow,
                TemplateVersionId: 0,
                EntityType: "cfg.PermitTypeDef",
                EntityId: type.Id,
                ChangeClass: ChangeClass.Guarded,
                Operation: "PublishPermitType",
                OldJson: null,
                NewJson: draftJson,
                ChangeReason: reason,
                ChangedByUserId: userId,
                CorrelationId: currentUser.CorrelationId),
            ct).ConfigureAwait(false);
    }

    private async Task ApplyHeaderAsync(
        PermitTypeDef type, RegistryDef header, PermitTypeContent content, string reason, int userId,
        Security.AccessProfile profile, Dictionary<int, PermitFieldMeta> metas, CancellationToken ct)
    {
        var fields = PermitHeaderFields.All
            .Select(f => new RegistryFieldSaveDto(
                IdOf(header, f.Code), f.Code, Localized(f.NameL10n), f.DataType, f.Order, f.Required, f.Key, null, null))
            .ToList();

        var keyDtos = await KeysAsync(header, [PermitHeaderFields.All[0].Code], ct).ConfigureAwait(false);
        var rules = await RulesAsync(header, content.Rules, ct).ConfigureAwait(false);

        await apply.ApplyAsync(header, new SaveRegistryDefinitionDto(fields, rules, reason, keyDtos), "PublishPermitType", userId, profile, ct)
            .ConfigureAwait(false);

        UpsertMetas(type, header, PermitHeaderFields.All, metas, listIds: new Dictionary<string, int>(), siblingDefs: null);
    }

    private async Task ApplyTableAsync(
        PermitTypeDef type, RegistryDef header, PermitTypeTableDto table, Dictionary<string, RegistryDef> tableDefs,
        string reason, int userId, Security.AccessProfile profile, Dictionary<int, PermitFieldMeta> metas, CancellationToken ct)
    {
        var def = tableDefs[table.Code];
        var listIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fields = new List<RegistryFieldSaveDto>
        {
            // Композиція: рядок належить дозволу-батькові. Для наявного поля відношення не повторюється (воно незмінне).
            new(
                IdOf(def, PermitHeaderFields.PermitFieldCode),
                PermitHeaderFields.PermitFieldCode,
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit", ["ru"] = "Разрешение" }),
                nameof(CellDataType.Lookup),
                0,
                true,
                false,
                header.Id,
                null,
                IdOf(def, PermitHeaderFields.PermitFieldCode) is null ? RegistryRelationKind.Composition : null,
                IdOf(def, PermitHeaderFields.PermitFieldCode) is null ? ParentDeletePolicy.Restrict : null),
        };

        foreach (var column in table.Columns)
        {
            int? target = null;
            if (string.Equals(column.DataType, nameof(CellDataType.Lookup), StringComparison.Ordinal))
            {
                target = await LookupTargetAsync(type, table, column, tableDefs, listIds, userId, profile, reason, ct).ConfigureAwait(false);
            }

            fields.Add(new RegistryFieldSaveDto(
                IdOf(def, column.Code), column.Code, Localized(column.NameL10n), column.DataType, column.Order,
                column.Required || column.Key, column.Key, target, column.UnitId));
        }

        var keyFieldCodes = new List<string> { PermitHeaderFields.PermitFieldCode };
        keyFieldCodes.AddRange(table.Columns.Where(c => c.Key).Select(c => c.Code));

        var keyDtos = await KeysAsync(def, keyFieldCodes, ct).ConfigureAwait(false);
        var rules = await RulesAsync(def, table.Rules, ct).ConfigureAwait(false);

        await apply.ApplyAsync(def, new SaveRegistryDefinitionDto(fields, rules, reason, keyDtos), "PublishPermitType", userId, profile, ct)
            .ConfigureAwait(false);

        UpsertMetas(type, def, table.Columns, metas, listIds, tableDefs);
    }

    /// <summary>Ціль поля Lookup за областю списку; список «лише для типу» при потребі створюється й наповнюється.</summary>
    private async Task<int> LookupTargetAsync(
        PermitTypeDef type, PermitTypeTableDto table, PermitTypeColumnDto column, Dictionary<string, RegistryDef> tableDefs,
        Dictionary<string, int> listIds, int userId, Security.AccessProfile profile, string reason, CancellationToken ct)
    {
        switch (Enum.Parse<PermitListScope>(column.ListScope!))
        {
            case PermitListScope.SharedRegistry:
                // Спільний довідник лише читається: його Id і версія опису лишаються як були.
                var shared = await registries.FindDefinitionAsync(column.ListRegistryCode!, ct).ConfigureAwait(false);
                if (shared is null || RegistryAccess.IsDenied(profile, shared.Id))
                {
                    throw PermitTypeDefinitionValidator.SharedRegistryUnknown(column.Code, column.ListRegistryCode!);
                }

                listIds[column.Code] = shared.Id;
                return shared.Id;

            case PermitListScope.TypeOnly:
                var listId = await EnsureListAsync(type, table, column, userId, profile, reason, ct).ConfigureAwait(false);
                listIds[column.Code] = listId;
                return listId;

            default:
                return tableDefs[column.SiblingTableCode!].Id;
        }
    }

    /// <summary>Прихований реєстр списку «лише для типу»: одне поле-ключ, значення - записи.</summary>
    private async Task<int> EnsureListAsync(
        PermitTypeDef type, PermitTypeTableDto table, PermitTypeColumnDto column, int userId,
        Security.AccessProfile profile, string reason, CancellationToken ct)
    {
        var listCode = PermitHeaderFields.ListRegistryCode(type.Code, table.Code, column.Code);
        var list = await registries.FindDefinitionAsync(listCode, ct).ConfigureAwait(false);

        if (list is null)
        {
            list = await NewRegistryAsync(listCode, column.NameL10n, isTemporal: false, ct).ConfigureAwait(false);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);

            var nameField = new RegistryFieldSaveDto(
                null, "Name", new LocalizedText(new Dictionary<string, string> { ["en"] = "Value" }), nameof(CellDataType.String), 1, false, true, null, null);
            await apply.ApplyAsync(list, new SaveRegistryDefinitionDto([nameField], [], reason), "PublishPermitType", userId, profile, ct)
                .ConfigureAwait(false);
        }
        else if (!(await IsOwnListAsync(type, list, ct).ConfigureAwait(false)))
        {
            // Реєстр з таким кодом уже є й належить не цьому стовпцю: чужого не чіпаємо.
            throw PermitTypeErrors.RegistryCodeTaken(listCode);
        }

        await SyncChoicesAsync(list, column.Choices ?? [], userId, ct).ConfigureAwait(false);
        return list.Id;
    }

    private async Task<bool> IsOwnListAsync(PermitTypeDef type, RegistryDef list, CancellationToken ct)
        => (await permits.ListFieldMetasAsync(type.Id, ct).ConfigureAwait(false))
            .Any(m => m.ListRegistryDefId == list.Id && m.ListScope == PermitListScope.TypeOnly);

    /// <summary>Значення списку: наявні за підписом лишаються, нові додаються, зайві вимикаються (записи не видаляються).</summary>
    private async Task SyncChoicesAsync(RegistryDef list, IReadOnlyList<string> choices, int userId, CancellationToken ct)
    {
        var entries = await registries.ListEntriesAsync(list.Id, ct).ConfigureAwait(false);
        var byText = entries
            .Where(e => !e.IsDeleted)
            .GroupBy(e => e.DisplayL10n.Get("en") ?? e.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var next = entries
            .Select(e => e.Code.Length > 1 && int.TryParse(e.Code.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        var wanted = new HashSet<string>(choices, StringComparer.OrdinalIgnoreCase);
        var ordinal = 1;
        foreach (var text in choices)
        {
            if (byText.TryGetValue(text, out var found))
            {
                var tracked = await registries.FindEntryAsync(found.Id, ct).ConfigureAwait(false);
                if (tracked is not null)
                {
                    if (!tracked.IsActive)
                    {
                        tracked.Activate();
                    }

                    tracked.SetOrdinal(ordinal);
                }
            }
            else
            {
                var entry = new RegistryEntry(
                    list.Id,
                    EcrCode.Create("V" + next.ToString("D3", CultureInfo.InvariantCulture)),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = text }),
                    userId,
                    clock.UtcNow);
                entry.SetOrdinal(ordinal);
                registries.Add(entry);
                next++;
            }

            ordinal++;
        }

        foreach (var entry in byText.Where(p => !wanted.Contains(p.Key)).Select(p => p.Value))
        {
            if (entry.IsActive && await registries.FindEntryAsync(entry.Id, ct).ConfigureAwait(false) is { } tracked)
            {
                tracked.Deactivate();
            }
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Складає надбудову полів: створює, оновлює або прибирає рядки <c>cfg.PermitFieldMeta</c>.</summary>
    private void UpsertMetas(
        PermitTypeDef type, RegistryDef def, IEnumerable<PermitTypeColumnDto> columns,
        Dictionary<int, PermitFieldMeta> metas, Dictionary<string, int> listIds, Dictionary<string, RegistryDef>? siblingDefs)
    {
        _ = siblingDefs;
        foreach (var column in columns)
        {
            var fieldId = IdOf(def, column.Code);
            if (fieldId is not { } id)
            {
                continue;
            }

            var scope = column.ListScope is null ? (PermitListScope?)null : Enum.Parse<PermitListScope>(column.ListScope);
            var any = column.Group is not null || column.Precision is not null || column.Scale is not null || scope is not null;
            metas.TryGetValue(id, out var current);

            if (!any)
            {
                if (current is not null)
                {
                    permits.Remove(current);
                    metas.Remove(id);
                }

                continue;
            }

            var group = column.Group is null ? (EcrCode?)null : EcrCode.Create(column.Group);
            var sibling = scope == PermitListScope.SamePermitTable ? EcrCode.Create(column.SiblingTableCode!) : (EcrCode?)null;
            int? listId = scope is PermitListScope.SharedRegistry or PermitListScope.TypeOnly ? listIds[column.Code] : null;

            if (current is null)
            {
                var created = new PermitFieldMeta(id, type.Id, group, column.Precision, column.Scale, scope, listId, sibling);
                permits.Add(created);
                metas[id] = created;
            }
            else
            {
                current.Update(group, column.Precision, column.Scale, scope, listId, sibling);
            }
        }
    }

    /// <summary>Первинний ключ: наявний повертається як є (склад незмінний), новий - з переданих кодів.</summary>
    private async Task<IReadOnlyList<RegistryKeySaveDto>> KeysAsync(RegistryDef def, IReadOnlyList<string> fieldCodes, CancellationToken ct)
    {
        var existing = (await keys.ListKeysForUpdateAsync(def.Id, ct).ConfigureAwait(false))
            .FirstOrDefault(k => k.IsPrimary && string.Equals(k.Code, PermitHeaderFields.PrimaryKeyCode, StringComparison.Ordinal));
        var name = new LocalizedText(new Dictionary<string, string> { ["en"] = "Primary key", ["ru"] = "Первичный ключ" });

        if (existing is null)
        {
            return [new RegistryKeySaveDto(null, PermitHeaderFields.PrimaryKeyCode, name, fieldCodes, true, true, true)];
        }

        var codeById = def.Fields.ToDictionary(f => f.Id, f => f.Code);
        return [new RegistryKeySaveDto(
            existing.Id, existing.Code, name, GetRegistryDefinitionHandler.KeyFieldCodes(existing, codeById), true, existing.IgnoreCase, true)];
    }

    /// <summary>Перевірки: наявні за кодом зберігають ідентифікатор, відсутні в чернетці вимикає ядро реєстрів.</summary>
    private async Task<IReadOnlyList<RegistryRuleSaveDto>> RulesAsync(RegistryDef def, IReadOnlyList<PermitTypeRuleDto> wanted, CancellationToken ct)
    {
        var existing = (await registries.ListRulesAsync(def.Id, ct).ConfigureAwait(false))
            .ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

        return [.. wanted.Select(r => new RegistryRuleSaveDto(
            existing.TryGetValue(r.Code, out var found) ? found.Id : null,
            r.Code,
            nameof(RegistryRuleKind.Expression),
            r.Expression,
            r.Severity == "Block" ? nameof(ValidationSeverity.Error) : nameof(ValidationSeverity.Warning),
            Localized(r.NameL10n),
            null,
            true))];
    }

    private async Task<RegistryDef> NewRegistryAsync(string registryCode, Dictionary<string, string> name, bool isTemporal, CancellationToken ct)
    {
        if (await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false) is not null)
        {
            throw PermitTypeErrors.RegistryCodeTaken(registryCode);
        }

        var created = new RegistryDef(EcrCode.Create(registryCode), Localized(name), isTemporal);
        registries.AddDefinition(created);
        return created;
    }

    private static int? IdOf(RegistryDef def, string fieldCode)
        => def.Fields.FirstOrDefault(f => string.Equals(f.Code, fieldCode, StringComparison.Ordinal))?.Id is > 0 and var id ? id : null;

    private static LocalizedText Localized(Dictionary<string, string> values)
        => new(new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
}
