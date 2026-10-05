// src/Ecr.Application/Sources/SourceEntityHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Заводить сутність збору з каталогу джерела (<c>ФВ-13.11</c>, <c>ФВ-13.13</c>).
/// Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ До цього обробника <c>new SourceEntity(</c> траплявся лише в тестах і сіді:
/// з вебу сутність збору не заводилась, тобто «сутність збору — дані, які
/// налаштовуються у вебі» (ФВ-13.11) лишалося написаним і нічиїм.
///
/// ⚠ Каталог джерела тут НЕ перечитується. Код, підпис і шлях приходять із
/// позиції каталогу, яку людина щойно обрала у формі
/// (<c>GET /data-sources/{id}/catalog</c>); повторне звернення до чужої
/// системи зробило б заведення конфігурації залежним від того, чи відповідає
/// PI саме зараз, — а збір за неіснуючим кодом і так видно в журналі покриття.
/// </remarks>
public sealed class CreateSourceEntityHandler(
    ICollectionStore sources,
    IDataSourceStore dataSources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Операція в журналі структурних змін (<c>ФВ-12.10</c>).</summary>
    public const string AuditOperation = "CreateSourceEntity";

    /// <summary>Стеля коду — ширина колонки <c>ext.SourceEntity.Code</c>.</summary>
    public const int MaxCodeLength = 200;

    /// <summary>Стеля підпису й шляху — ширина колонок <c>DisplayName</c>/<c>EntityPath</c>.</summary>
    public const int MaxTextLength = 400;

    /// <summary>Заводить сутність.</summary>
    /// <param name="command">Позиція каталогу й з'єднання.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">З'єднання немає.</exception>
    /// <exception cref="BusinessRuleException">
    /// Код порожній чи задовгий (<c>ECR-REQ-0422</c>) або вже зайнятий у цьому
    /// з'єднанні (<c>ECR-INT-0409</c>).
    /// </exception>
    public async Task<SourceEntityDto> HandleAsync(CreateSourceEntityCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);

        var code = command.Code?.Trim() ?? string.Empty;
        var kind = command.SourceKind ?? RegistrySourceKind.External;

        if (code.Length is 0 or > MaxCodeLength
            || command.DisplayName?.Length > MaxTextLength
            || command.EntityPath?.Length > MaxTextLength
            || !Enum.IsDefined(kind))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid,
                $"Код сутності — від 1 до {MaxCodeLength} символів, підпис і шлях — до {MaxTextLength}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.sourceEntityInvalid",
                    ["maxCode"] = MaxCodeLength,
                    ["maxText"] = MaxTextLength,
                });
        }

        var dataSource = await ListDataSourcesHandler
            .FindAsync(dataSources, command.DataSourceId, ct)
            .ConfigureAwait(false);

        // ⚠ Перевірка ДО запису, а не спіймане порушення UQ_SourceEntity:
        // інакше відмова доїхала б як збій бази, а не як «такий код уже є».
        if (await sources.SourceEntityCodeExistsAsync(dataSource.Id, code, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                ErrorCodes.EntityFieldMapStateConflict,
                $"У з'єднання «{dataSource.Code}» вже є сутність збору з кодом «{code}».",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0409.sourceEntityDuplicate",
                    ["code"] = code,
                    ["dataSource"] = dataSource.Code,
                });
        }

        var entity = new SourceEntity(dataSource.Id, code, kind);
        entity.Describe(Blank(command.DisplayName), Blank(command.EntityPath));

        // ФВ-12.10: заведення сутності збору лишає слід у журналі структурних змін (в одній транзакції).
        SourceEntity? created = null;
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            created = await sources.AddSourceEntityAsync(entity, innerCt).ConfigureAwait(false);

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.SourceEntityType, created.Id, AuditOperation,
                oldJson: null, newJson: IntegrationConfigAudit.Snapshot(created),
                reason: IntegrationConfigAudit.Reason("integrationAudit.sourceEntityCreated", ("entity", created.Code), ("connection", dataSource.Code)), innerCt)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return SourceEntityDto.From(created!);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Прив'язує сутність збору до довідника або відв'язує її (<c>ФВ-8.11</c>).
/// Право <c>Integration.Manage</c> і право редагувати дані довідника
/// (<c>Registry.EditData</c> або грант <c>Write</c>) — цільового й поточного.
/// </summary>
/// <remarks>
/// ⚠ Прив'язка — передумова мапінгу на поле довідника: саме за нею
/// <see cref="CreateEntityFieldMapHandler"/> відрізняє «своє» поле від поля
/// сусіднього довідника.
/// </remarks>
public sealed class BindSourceEntityRegistryHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція в журналі структурних змін (<c>ФВ-12.10</c>).</summary>
    public const string AuditOperation = "BindSourceEntityRegistry";

    /// <summary>Прив'язує (<paramref name="registryDefId"/>) або відв'язує (<c>null</c>).</summary>
    /// <param name="sourceEntityId">Сутність збору.</param>
    /// <param name="registryDefId">Довідник; <c>null</c> — відв'язати.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сутності чи довідника немає.</exception>
    /// <exception cref="AccessDeniedException">
    /// Немає <c>Integration.Manage</c> або права на дані цільового чи поточного довідника.
    /// </exception>
    public async Task<SourceEntityDto> HandleAsync(int sourceEntityId, int? registryDefId, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, CreateSourceEntityHandler.Permission, ct)
            .ConfigureAwait(false);

        var entity = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                    ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });

        if (registryDefId is { } id
            && !await sources.RegistryDefExistsAsync(id, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.RegistryEntryNotFound,
                $"Довідника {id} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REG-0404.registryId",
                    ["registryDefId"] = id.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Одного Integration.Manage мало: після прив'язки синк пише в дані
        // довідника від імені svc-integration (FEATURE-REGISTRY-SYNC §3), тобто
        // прив'язка — це делегування права редагувати дані довідника. Тому
        // вимагається те саме, що й для запису даних цього довідника
        // (UpsertRegistryEntryHandler): Registry.EditData АБО грант Write на
        // RegistryDefId. Відв'язка й переприв'язка знімають синк із ПОТОЧНОГО
        // довідника — та сама вимога щодо нього. Судження розробки (D-202,
        // доповнення 2026-09-29), на підтвердження людиною.
        // Після 404 на неіснуючий довідник — порядок відмов лишається тим самим.
        if (registryDefId is { } target)
        {
            await RegistryAccess
                .RequireAsync(access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write, target, ct)
                .ConfigureAwait(false);
        }

        if (entity.RegistryDefId is { } current && current != registryDefId)
        {
            await RegistryAccess
                .RequireAsync(access, currentUser, UpsertRegistryEntryHandler.Permission, GrantLevel.Write, current, ct)
                .ConfigureAwait(false);
        }

        // ⛔ AN-34 L4-01: один довідник - одна сутність у з'єднанні. Зв'язки синку довідника
        // (dic.RegistryExternalKey) тримаються за парою (з'єднання, довідник) без сутності, тож
        // друга сутність змусила б кожну вважати зв'язки іншої «зниклими» й вимикати чужі записи
        // щопрогону. Перевірка ДО запису, а не спіймане порушення UQ_SourceEntity_Registry (його
        // ловить лише гонку - CollectionStore.SaveSourceEntityAsync). Після перевірок прав: чужу
        // прив'язку бачить лише той, кому дозволено прив'язувати.
        if (registryDefId is { } bindTo
            && entity.RegistryDefId != bindTo
            && await sources.RegistryBoundByOtherEntityAsync(entity.DataSourceId, bindTo, entity.Id, ct).ConfigureAwait(false))
        {
            throw AlreadyBound(entity.DataSourceId, bindTo);
        }

        var before = IntegrationConfigAudit.Snapshot(entity);
        entity.BindRegistry(registryDefId);

        // ФВ-12.10: прив'язка/відв'язка довідника — зміна мапінгу на рівні сутності; старий і новий стан у журналі.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            await sources.SaveSourceEntityAsync(entity, innerCt).ConfigureAwait(false);

            // Повторна прив'язка до того самого довідника нічого не змінила — шуму в журналі не пишемо.
            var after = IntegrationConfigAudit.Snapshot(entity);
            if (string.Equals(before, after, StringComparison.Ordinal))
            {
                return;
            }

            await IntegrationConfigAudit.WriteAsync(
                audit, clock, currentUser, IntegrationConfigAudit.SourceEntityType, entity.Id, AuditOperation,
                before, after,
                reason: registryDefId is null
                    ? IntegrationConfigAudit.Reason("integrationAudit.sourceEntityUnbound", ("entity", entity.Code))
                    : IntegrationConfigAudit.Reason("integrationAudit.sourceEntityBound", ("entity", entity.Code), ("registry", registryDefId)),
                innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return SourceEntityDto.From(entity);
    }

    /// <summary>Ключ каталогу: довідник цього з'єднання вже тримає інша сутність збору.</summary>
    public const string RegistryAlreadyBoundKey = "err.ECR-INT-0409.registryAlreadyBound";

    /// <summary>
    /// Відмова «довідник уже прив'язаний до іншої сутності цього з'єднання» (<c>409 ECR-INT-0409</c>):
    /// спільна для перевірки обробника й для порушення <c>UQ_SourceEntity_Registry</c> у сховищі.
    /// </summary>
    /// <param name="dataSourceId">З'єднання.</param>
    /// <param name="registryDefId">Довідник.</param>
    public static BusinessRuleException AlreadyBound(int dataSourceId, int registryDefId)
        => new(
            ErrorCodes.EntityFieldMapStateConflict,
            $"Довідник {registryDefId} уже прив'язаний до іншої сутності збору цього з'єднання ({dataSourceId}): " +
            "одному довіднику - одна сутність на з'єднання; спершу відв'яжіть іншу.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RegistryAlreadyBoundKey,
                ["registryDefId"] = registryDefId.ToString(CultureInfo.InvariantCulture),
                ["dataSourceId"] = dataSourceId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>Налаштування нової сутності збору — позиція каталогу джерела.</summary>
/// <param name="DataSourceId">З'єднання.</param>
/// <param name="Code">Код у джерелі (<c>SourceCatalogItem.Code</c>).</param>
/// <param name="DisplayName">Підпис із каталогу.</param>
/// <param name="EntityPath">Шлях в ієрархії джерела.</param>
/// <param name="SourceKind">Хто master (ФВ-8.9); <c>null</c> — <c>External</c>, як у схемі.</param>
public sealed record CreateSourceEntityCommand(
    int DataSourceId,
    string? Code,
    string? DisplayName,
    string? EntityPath,
    RegistrySourceKind? SourceKind);

/// <summary>Сутність збору у відповіді на заведення чи прив'язку.</summary>
/// <param name="Id">Ідентифікатор <c>ext.SourceEntity</c>.</param>
/// <param name="DataSourceId">З'єднання.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис.</param>
/// <param name="EntityPath">Шлях в ієрархії.</param>
/// <param name="SourceKind">Хто master.</param>
/// <param name="RegistryDefId">Довідник; <c>null</c> — не прив'язана.</param>
/// <param name="IsActive">Чи ввімкнено збір.</param>
/// <param name="OnMissingInSource">Політика синку: зникнення елемента в джерелі (<c>D-212</c>).</param>
/// <param name="ValidFromAttribute">Атрибут початку чинності; <c>null</c> — не синхронізується.</param>
/// <param name="ValidToAttribute">Атрибут кінця чинності; <c>null</c> — не синхронізується.</param>
/// <param name="ValidToInclusive">Кінець у джерелі — останній чинний день.</param>
public sealed record SourceEntityDto(
    int Id,
    int DataSourceId,
    string Code,
    string? DisplayName,
    string? EntityPath,
    RegistrySourceKind SourceKind,
    int? RegistryDefId,
    bool IsActive,
    RegistryMissingPolicy OnMissingInSource,
    string? ValidFromAttribute,
    string? ValidToAttribute,
    bool ValidToInclusive)
{
    /// <summary>DTO з сутності.</summary>
    /// <param name="entity">Сутність.</param>
    public static SourceEntityDto From(SourceEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new(
            entity.Id, entity.DataSourceId, entity.Code, entity.DisplayName, entity.EntityPath,
            entity.SourceKind, entity.RegistryDefId, entity.IsActive,
            entity.OnMissingInSource, entity.ValidFromAttribute, entity.ValidToAttribute, entity.ValidToInclusive);
    }
}
