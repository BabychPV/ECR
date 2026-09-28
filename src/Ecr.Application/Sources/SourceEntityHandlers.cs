// src/Ecr.Application/Sources/SourceEntityHandlers.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

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

        var created = await sources.AddSourceEntityAsync(entity, ct).ConfigureAwait(false);

        return SourceEntityDto.From(created);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Прив'язує сутність збору до довідника або відв'язує її (<c>ФВ-8.11</c>).
/// Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ Прив'язка — передумова мапінгу на поле довідника: саме за нею
/// <see cref="CreateEntityFieldMapHandler"/> відрізняє «своє» поле від поля
/// сусіднього довідника.
/// </remarks>
public sealed class BindSourceEntityRegistryHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Прив'язує (<paramref name="registryDefId"/>) або відв'язує (<c>null</c>).</summary>
    /// <param name="sourceEntityId">Сутність збору.</param>
    /// <param name="registryDefId">Довідник; <c>null</c> — відв'язати.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Сутності чи довідника немає.</exception>
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

        entity.BindRegistry(registryDefId);
        await sources.SaveSourceEntityAsync(entity, ct).ConfigureAwait(false);

        return SourceEntityDto.From(entity);
    }
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
public sealed record SourceEntityDto(
    int Id,
    int DataSourceId,
    string Code,
    string? DisplayName,
    string? EntityPath,
    RegistrySourceKind SourceKind,
    int? RegistryDefId,
    bool IsActive)
{
    /// <summary>DTO з сутності.</summary>
    /// <param name="entity">Сутність.</param>
    public static SourceEntityDto From(SourceEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new(
            entity.Id, entity.DataSourceId, entity.Code, entity.DisplayName, entity.EntityPath,
            entity.SourceKind, entity.RegistryDefId, entity.IsActive);
    }
}
