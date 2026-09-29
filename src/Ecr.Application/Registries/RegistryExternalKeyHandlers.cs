// src/Ecr.Application/Registries/RegistryExternalKeyHandlers.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Registries;

/// <summary>
/// Зовнішні ідентифікатори записів довідника (<c>ФВ-8.10</c>, FEATURE-REGISTRY-SYNC S2): перелік.
/// Право <c>Registry.View</c> або грант <c>Read</c> на довідник.
/// </summary>
/// <remarks>
/// ⛔ До S2 <c>new RegistryExternalKey(</c> не траплявся в коді взагалі: синк
/// (<c>RegistrySyncJob.LinksAsync</c>) читав зв'язки, яких ніхто не міг завести.
/// </remarks>
public sealed class ListRegistryExternalKeysHandler(
    IRegistryStore registries,
    IRegistryExternalKeyStore keys,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Сторінка зв'язків довідника.</summary>
    /// <param name="registryCode">Довідник зі шляху запиту.</param>
    /// <param name="entryId">Лише зв'язки цього запису.</param>
    /// <param name="dataSourceId">Лише зв'язки цього джерела.</param>
    /// <param name="page">Розмір сторінки 1..200 і курсор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника немає.</exception>
    /// <exception cref="BusinessRuleException">Розмір сторінки поза межами.</exception>
    public async Task<PagedResult<RegistryExternalKeyView>> HandleAsync(
        string registryCode, long? entryId, int? dataSourceId, CursorRequest page, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentNullException.ThrowIfNull(page);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess
            .RequireAsync(access, currentUser, GetRegistryEntriesHandler.Permission, GrantLevel.Read, lookup.IdAsync, ct)
            .ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);

        ListCollectionRunsHandler.RequirePageSize(page);

        return await keys
            .ListAsync(new RegistryExternalKeyFilter(definition.Id, entryId, dataSourceId), page, ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Прив'язує запис довідника до елемента зовнішнього джерела. Право
/// <c>Registry.EditData</c> або грант <c>Write</c> на довідник.
/// </summary>
/// <remarks>
/// ⚠ Право — на ДАНІ довідника, без <c>Integration.Manage</c> (судження S2): зв'язок —
/// властивість запису, як і значення його полів. Делегування запису синку — це
/// прив'язка <c>SourceEntity</c> до довідника (<c>PUT /sources/{id}/registry</c>), і там
/// <c>Integration.Manage</c> уже вимагається (<c>D-202</c>, доповнення 2026-09-29).
/// </remarks>
public sealed class BindRegistryExternalKeyHandler(
    IRegistryStore registries,
    IRegistryExternalKeyStore keys,
    IDataSourceStore dataSources,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Стеля ідентифікатора — ширина колонки <c>dic.RegistryExternalKey.ExternalId</c>.</summary>
    public const int MaxExternalIdLength = 200;

    /// <summary>Прив'язує.</summary>
    /// <param name="registryCode">Довідник зі шляху запиту.</param>
    /// <param name="command">Запис, джерело, ідентифікатор.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">
    /// Довідника, запису (зокрема видаленого чи з іншого довідника) або джерела немає.
    /// </exception>
    /// <exception cref="BusinessRuleException">
    /// Порожній чи задовгий ідентифікатор (<c>ECR-REQ-0422</c>) або пара
    /// «джерело + ідентифікатор» уже зайнята (<c>ECR-REG-0409</c>).
    /// </exception>
    public async Task<RegistryExternalKeyView> HandleAsync(
        string registryCode, BindRegistryExternalKeyCommand command, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);
        ArgumentNullException.ThrowIfNull(command);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess
            .RequireAsync(access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write, lookup.IdAsync, ct)
            .ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        var userId = RegistryExternalKeyRules.UserId(currentUser);

        var externalId = command.ExternalId?.Trim() ?? string.Empty;
        if (externalId.Length is 0 or > MaxExternalIdLength)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Зовнішній ідентифікатор — від 1 до {MaxExternalIdLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.externalKeyInvalid",
                    ["max"] = MaxExternalIdLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Запис має належати довіднику зі ШЛЯХУ: право перевірено саме на нього,
        // а Id запису наскрізний по всіх довідниках. Без цієї звірки грант на один
        // довідник давав би прив'язувати записи будь-якого іншого. Видалений запис —
        // той самий 404, що й у writer'а (`err.ECR-REG-0404.registryEntry`).
        var entry = await registries.FindEntryAsync(command.EntryId, ct).ConfigureAwait(false);
        if (entry is null || entry.IsDeleted || entry.RegistryDefId != definition.Id)
        {
            throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Запису {command.EntryId} у довіднику «{definition.Code}» не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryEntry",
                    ["entryId"] = command.EntryId.ToString(CultureInfo.InvariantCulture),
                    ["registryCode"] = definition.Code,
                });
        }

        var dataSource = await ListDataSourcesHandler
            .FindAsync(dataSources, command.DataSourceId, ct)
            .ConfigureAwait(false);

        // ⚠ Перевірка ДО запису: інакше дубль доїхав би збоєм UQ_RegistryExternalKey.
        // Гонку двох одночасних прив'язок закриває сховище тим самим кодом.
        var taken = await keys.FindByExternalIdAsync(dataSource.Id, externalId, ct).ConfigureAwait(false);
        if (taken is not null)
        {
            throw RegistryExternalKeyRules.Taken(dataSource.Code, externalId, taken.EntryCode);
        }

        var key = new RegistryExternalKey(entry.Id, dataSource.Id, externalId);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await keys.AddAsync(key, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                RegistryExternalKeyRules.Change(
                    clock.UtcNow, key.Id, "Bind", ChangeClass.Safe, oldJson: null,
                    newJson: RegistryExternalKeyRules.Json(definition.Code, entry.Code, dataSource.Id, externalId),
                    $"Запис «{entry.Code}» довідника «{definition.Code}» прив'язано до «{externalId}» джерела «{dataSource.Code}».",
                    userId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return new RegistryExternalKeyView(
            key.Id, entry.Id, entry.Code, dataSource.Id, dataSource.Code, key.ExternalId, key.ExternalPath, key.LastSyncedAt);
    }
}

/// <summary>
/// Відв'язує запис довідника від елемента джерела. Право — як на прив'язку.
/// </summary>
/// <remarks>
/// ⚠ Відв'язка можлива й для видаленого запису: прибрати зв'язок, який синк більше
/// не має чіпати, — саме те, що з таким записом і треба зробити.
/// </remarks>
public sealed class UnbindRegistryExternalKeyHandler(
    IRegistryStore registries,
    IRegistryExternalKeyStore keys,
    IUnitOfWork uow,
    IAuditWriter audit,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Відв'язує.</summary>
    /// <param name="registryCode">Довідник зі шляху запиту.</param>
    /// <param name="keyId">Зв'язок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Довідника або зв'язку в ЦЬОМУ довіднику немає.</exception>
    public async Task HandleAsync(string registryCode, long keyId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryCode);

        var lookup = new RegistryLookup(registries, registryCode);
        await RegistryAccess
            .RequireAsync(access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write, lookup.IdAsync, ct)
            .ConfigureAwait(false);
        var definition = await lookup.RequireAsync(ct).ConfigureAwait(false);
        var userId = RegistryExternalKeyRules.UserId(currentUser);

        var key = await keys.FindAsync(keyId, ct).ConfigureAwait(false);

        // ⛔ Той самий принцип, що в `DeleteRegistryEntryHandler`: зв'язок запису
        // ЧУЖОГО довідника для того, хто питає, не існує — 404, а не видалення.
        var entry = key is null ? null : await registries.FindEntryAsync(key.RegistryEntryId, ct).ConfigureAwait(false);
        if (key is null || entry is null || entry.RegistryDefId != definition.Id)
        {
            throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Зовнішнього ідентифікатора {keyId} у довіднику «{definition.Code}» немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.externalKey",
                    ["id"] = keyId.ToString(CultureInfo.InvariantCulture),
                    ["registryCode"] = definition.Code,
                });
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await keys.RemoveAsync(key, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                RegistryExternalKeyRules.Change(
                    clock.UtcNow, keyId, "Unbind", ChangeClass.Guarded,
                    oldJson: RegistryExternalKeyRules.Json(definition.Code, entry.Code, key.DataSourceId, key.ExternalId),
                    newJson: null,
                    $"Запис «{entry.Code}» довідника «{definition.Code}» відв'язано від «{key.ExternalId}».",
                    userId, currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}

/// <summary>Тіло прив'язки.</summary>
/// <param name="EntryId">Запис довідника.</param>
/// <param name="DataSourceId">Джерело (<c>ext.DataSource</c>).</param>
/// <param name="ExternalId">Ідентифікатор у джерелі (WebId/GUID); до 200 символів.</param>
public sealed record BindRegistryExternalKeyCommand(long EntryId, int DataSourceId, string? ExternalId);

/// <summary>
/// Довідник зі шляху запиту: читається щонайбільше раз — або ліниво всередині
/// <see cref="RegistryAccess"/> (немає глобального права), або після перевірки права.
/// </summary>
/// <remarks>
/// ⚠ Порядок той самий, що в решти маршрутів довідників: спершу право, потім 404.
/// Без права відповідь — <c>403</c>, і про існування довідника вона не каже.
/// Право обробник передає САМ, константою: сторож <c>ProjectPermissionCheckTests</c>
/// читає код права в місці виклику <see cref="RegistryAccess.RequireAsync(IAccessDecisionService, ICurrentUser, string, GrantLevel, Func{CancellationToken, Task{int?}}, CancellationToken)"/>.
/// </remarks>
internal sealed class RegistryLookup(IRegistryStore registries, string registryCode)
{
    private RegistryDef? _definition;
    private bool _loaded;

    /// <summary>Резолвер для <see cref="RegistryAccess"/>.</summary>
    internal async Task<int?> IdAsync(CancellationToken ct) => (await LoadAsync(ct).ConfigureAwait(false))?.Id;

    /// <summary>Довідник або <c>404 err.ECR-REG-0404.registry</c>.</summary>
    internal async Task<RegistryDef> RequireAsync(CancellationToken ct)
        => await LoadAsync(ct).ConfigureAwait(false)
           ?? throw new NotFoundException(
               ErrorCodes.RegistryEntryNotFound,
               $"Довідника «{registryCode}» не існує.",
               new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-REG-0404.registry",
                   ["registryCode"] = registryCode,
               });

    private async Task<RegistryDef?> LoadAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _definition = await registries.FindDefinitionAsync(registryCode, ct).ConfigureAwait(false);
            _loaded = true;
        }

        return _definition;
    }
}

/// <summary>Спільне для трьох обробників зовнішніх ключів.</summary>
internal static class RegistryExternalKeyRules
{

    internal static int UserId(ICurrentUser currentUser)
        => currentUser.UserId
           ?? throw new AccessDeniedException(
               "ECR-AUTH-0401",
               "Анонімний запит не змінює довідники.",
               new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

    /// <summary>Пара «джерело + ідентифікатор» уже прив'язана — <c>409 ECR-REG-0409</c>.</summary>
    internal static BusinessRuleException Taken(string dataSourceCode, string externalId, string entryCode)
        => new(
            ErrorCodes.RegistryEntryInUse,
            $"Ідентифікатор «{externalId}» джерела «{dataSourceCode}» уже прив'язано до запису «{entryCode}».",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-REG-0409.externalKeyTaken",
                ["externalId"] = externalId,
                ["dataSource"] = dataSourceCode,
                ["code"] = entryCode,
            });

    internal static string Json(string registryCode, string entryCode, int dataSourceId, string externalId)
        => JsonSerializer.Serialize(new { registry = registryCode, code = entryCode, dataSourceId, externalId });

    internal static StructureChangeRecord Change(
        DateTime changedAt,
        long keyId,
        string operation,
        ChangeClass changeClass,
        string? oldJson,
        string? newJson,
        string reason,
        int userId,
        string? correlationId)
        => new(
            ChangedAt: changedAt,
            TemplateVersionId: 0,
            EntityType: "dic.RegistryExternalKey",
            EntityId: checked((int)keyId),
            ChangeClass: changeClass,
            Operation: operation,
            OldJson: oldJson,
            NewJson: newJson,
            ChangeReason: reason,
            ChangedByUserId: userId,
            CorrelationId: correlationId);
}
