// src/Ecr.Application/Sources/SourceEntityRegistryPolicyHandler.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Політика синку довідника з AF для сутності збору (<c>D-212</c>, PR-2).
/// Право <c>Integration.Manage</c> і право на дані прив'язаного довідника
/// (<c>Registry.EditData</c> або грант <c>Write</c>).
/// </summary>
/// <remarks>
/// ⛔ Одного <c>Integration.Manage</c> мало з тієї самої причини, що й для
/// прив'язки (<see cref="BindSourceEntityRegistryHandler"/>): політика
/// <c>Deactivate</c> означає, що синк від імені <c>svc-integration</c>
/// вимикатиме записи довідника. Це зміна його даних — право те саме.
///
/// ⚠ Порядок відмов: 403 на <c>Integration.Manage</c> → 404 сутності → 422
/// «не прив'язана» → 403 на довідник → 422 перевірки тіла → 422 дат для
/// нетемпорального довідника (<see cref="NotTemporalKey"/>, PR-7). Тіло перевіряється
/// ПІСЛЯ права: відмова з подробицями валідації тому, хто не має права
/// змінювати, — зайва інформація.
///
/// ⚠ If-Match не вимагається: у <c>ext.SourceEntity</c> немає <c>RowVersion</c>,
/// і сусідній <c>PUT /sources/{id}/registry</c> теж без нього. Політика —
/// повна заміна чотирьох полів, «останній запис виграє», слід — у журналі.
/// </remarks>
public sealed class SetSourceEntityRegistryPolicyHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    IUnitOfWork uow,
    IAuditWriter audit,
    ICurrentUser currentUser,
    IClock clock,
    IRegistryStore registries)
{
    /// <summary>Ключ відмови: атрибути дат дії для нетемпорального довідника (<c>D-212</c> PR-7).</summary>
    public const string NotTemporalKey = "err.ECR-REQ-0422.registrySyncPolicyNotTemporal";

    /// <summary>Операція в журналі структурних змін.</summary>
    public const string AuditOperation = "SetRegistrySyncPolicy";

    /// <summary>Тип сутності в журналі структурних змін.</summary>
    public const string AuditEntityType = "ext.SourceEntity";

    /// <summary>Замінює політику синку довідника.</summary>
    /// <param name="sourceEntityId">Сутність збору.</param>
    /// <param name="command">Нова політика цілком.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="AccessDeniedException">
    /// Немає <c>Integration.Manage</c> або права на дані прив'язаного довідника.
    /// </exception>
    /// <exception cref="NotFoundException">Сутності немає або вона вимкнена.</exception>
    /// <exception cref="BusinessRuleException">Сутність не прив'язана до довідника.</exception>
    /// <exception cref="Domain.Abstractions.DomainException">Тіло не проходить перевірку.</exception>
    public async Task<SourceEntityDto> HandleAsync(
        int sourceEntityId, SetRegistrySyncPolicyCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck
            .RequireAsync(access, currentUser, CreateSourceEntityHandler.Permission, ct)
            .ConfigureAwait(false);

        var userId = RegistryExternalKeyRules.UserId(currentUser);

        var entity = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                    ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });

        // Політика синку ДОВІДНИКА без довідника нічого не означає: зберегти
        // її «на потім» — це налаштування, дію якого ніхто не побачить.
        if (entity.RegistryDefId is not { } registryDefId)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Сутність збору {sourceEntityId} не прив'язана до довідника: політика синку не має до чого застосуватися.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.registrySyncPolicyNotBound",
                    ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });
        }

        await RegistryAccess
            .RequireAsync(access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write, registryDefId, ct)
            .ConfigureAwait(false);

        var before = Json(entity);

        entity.ConfigureRegistrySync(
            command.OnMissingInSource, command.ValidFromAttribute, command.ValidToAttribute, command.ValidToInclusive);

        // D-212 PR-7: вікно дії є лише в записів ТЕМПОРАЛЬНОГО довідника. Атрибути дат для
        // нетемпорального — налаштування, дії якого не буде: синк їх не читає, і адміністратор
        // шукав би, чому дати AF «не доходять». Після перевірки тіла — порядок відмов з remarks.
        if ((entity.ValidFromAttribute is not null || entity.ValidToAttribute is not null)
            && await registries.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false) is { IsTemporal: false } registry)
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Довідник «{registry.Code}» не темпоральний: атрибути дат дії синку йому не застосовні.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = NotTemporalKey,
                    ["registry"] = registry.Code,
                });
        }

        var after = Json(entity);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await sources.SaveSourceEntityAsync(entity, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    ChangedAt: clock.UtcNow,
                    TemplateVersionId: 0,
                    EntityType: AuditEntityType,
                    EntityId: entity.Id,
                    ChangeClass: ChangeClass.Safe,
                    Operation: AuditOperation,
                    OldJson: before,
                    NewJson: after,
                    ChangeReason: $"Політика синку довідника {registryDefId} із сутності «{entity.Code}» змінена.",
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return SourceEntityDto.From(entity);
    }

    private static string Json(SourceEntity e) => JsonSerializer.Serialize(new
    {
        registryDefId = e.RegistryDefId,
        onMissingInSource = e.OnMissingInSource.ToString(),
        validFromAttribute = e.ValidFromAttribute,
        validToAttribute = e.ValidToAttribute,
        validToInclusive = e.ValidToInclusive,
    });
}

/// <summary>Нова політика синку довідника (<c>D-212</c>) — цілком, не латка.</summary>
/// <param name="OnMissingInSource">Що робити з записом, чий елемент зник у джерелі.</param>
/// <param name="ValidFromAttribute">Атрибут початку чинності; <c>null</c> — не синхронізувати.</param>
/// <param name="ValidToAttribute">Атрибут кінця чинності; <c>null</c> — не синхронізувати.</param>
/// <param name="ValidToInclusive">Кінець у джерелі — останній чинний день.</param>
public sealed record SetRegistrySyncPolicyCommand(
    RegistryMissingPolicy OnMissingInSource,
    string? ValidFromAttribute,
    string? ValidToAttribute,
    bool ValidToInclusive);
