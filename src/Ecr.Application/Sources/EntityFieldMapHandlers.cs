// src/Ecr.Application/Sources/EntityFieldMapHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Заводить мапінг поля джерела на колонку документа або поле реєстру
/// (<c>ext.EntityFieldMap</c>). Право <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⛔ Прогалина 1 директиви паритету зі старою системою. До цього обробника
/// <see cref="EntityFieldMap"/> заводився лише двома статичними фабриками
/// домену (<see cref="EntityFieldMap.ToColumn"/>,
/// <see cref="EntityFieldMap.ToRegistryField"/>), і обидві викликалися
/// виключно з тестів — жодного шляху АПІ до створення не було. Інженер, що
/// щойно додав нову колонку через <c>SaveColumnDefHandler</c>, не мав способу
/// завести для неї мапінг джерела інакше, ніж ручним SQL. Єдиний наявний
/// обробник над мапінгами — <see cref="PreviewMappingHandler"/> — READ-ONLY.
///
/// ⚠ За зразком <c>SaveColumnDefHandler</c>: право перевіряється першим,
/// існування сутностей-цілей — до запису, а не після (той самий порядок, що в
/// <c>CollectFromSourceHandler</c>).
/// </remarks>
public sealed class CreateEntityFieldMapHandler(
    ICollectionStore sources,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на керування інтеграцією (`02-contracts.md` §9).</summary>
    public const string Permission = "Integration.Manage";

    /// <summary>Заводить мапінг.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="command">Налаштування мапінгу.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">
    /// Сутності джерела, колонки, поля реєстру або одиниці немає.
    /// </exception>
    /// <exception cref="BusinessRuleException">
    /// Команда не називає поле джерела, або ціль не відповідає
    /// <see cref="CreateEntityFieldMapCommand.TargetKind"/>.
    /// </exception>
    public async Task<EntityFieldMapDto> HandleAsync(
        int sourceEntityId, CreateEntityFieldMapCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(command.SourceField))
        {
            throw new BusinessRuleException(
                ErrorCodes.RequestInvalid, "Поле джерела (sourceField) не може бути порожнім.");
        }

        // ⚠ Існування сутності джерела перевіряється ТУТ — так само, як у
        // CollectFromSourceHandler: мапінг на неіснуючу чи вимкнену сутність
        // виглядав би заведеним, а збір за ним не запустився б ніколи.
        _ = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.");

        var map = await BuildTargetAsync(sourceEntityId, command, ct).ConfigureAwait(false);

        await ApplyUnitsAsync(map, command, ct).ConfigureAwait(false);

        if (command.TargetRowKey is not null || command.Aggregation is not null)
        {
            // ⛔ Домен сам відхиляє рядок без агрегації (`ECR-INT-0422`) —
            // тут її НЕ дублюють, а лишень пробрасують обидва значення разом.
            map.SetMaterialization(command.TargetRowKey, command.Aggregation);
        }

        var created = await sources.AddFieldMapAsync(map, ct).ConfigureAwait(false);

        return Map(created);
    }

    /// <summary>Будує мапінг на потрібний вид цілі, перевіривши, що вона існує.</summary>
    private async Task<EntityFieldMap> BuildTargetAsync(
        int sourceEntityId, CreateEntityFieldMapCommand command, CancellationToken ct)
    {
        switch (command.TargetKind)
        {
            case FieldTargetKind.Column:
                if (command.TargetRegistryFieldDefId is not null)
                {
                    throw new BusinessRuleException(
                        ErrorCodes.RequestInvalid,
                        "Мапінг на колонку (targetKind=Column) не приймає targetRegistryFieldDefId.");
                }

                var columnDefId = command.TargetColumnDefId
                    ?? throw new BusinessRuleException(
                        ErrorCodes.RequestInvalid,
                        "Мапінг на колонку (targetKind=Column) вимагає targetColumnDefId.");

                if (!await sources.ColumnDefExistsAsync(columnDefId, ct).ConfigureAwait(false))
                {
                    throw new NotFoundException(
                        ErrorCodes.EntityFieldMapTargetNotFound,
                        $"Колонки {columnDefId} немає, або її видалено.");
                }

                return EntityFieldMap.ToColumn(sourceEntityId, command.SourceField, columnDefId);

            case FieldTargetKind.RegistryField:
                if (command.TargetColumnDefId is not null)
                {
                    throw new BusinessRuleException(
                        ErrorCodes.RequestInvalid,
                        "Мапінг на поле реєстру (targetKind=RegistryField) не приймає targetColumnDefId.");
                }

                var registryFieldDefId = command.TargetRegistryFieldDefId
                    ?? throw new BusinessRuleException(
                        ErrorCodes.RequestInvalid,
                        "Мапінг на поле реєстру (targetKind=RegistryField) вимагає targetRegistryFieldDefId.");

                if (!await sources.RegistryFieldDefExistsAsync(registryFieldDefId, ct).ConfigureAwait(false))
                {
                    throw new NotFoundException(
                        ErrorCodes.EntityFieldMapTargetNotFound,
                        $"Поля реєстру {registryFieldDefId} немає.");
                }

                return EntityFieldMap.ToRegistryField(sourceEntityId, command.SourceField, registryFieldDefId);

            default:
                throw new BusinessRuleException(
                    ErrorCodes.RequestInvalid, $"Невідомий вид цілі мапінгу: {command.TargetKind}.");
        }
    }

    /// <summary>Перевіряє й ставить одиниці межі інтеграції (ФВ-16.9), якщо їх названо.</summary>
    private async Task ApplyUnitsAsync(EntityFieldMap map, CreateEntityFieldMapCommand command, CancellationToken ct)
    {
        if (command.SourceUnitId is null && command.TargetUnitId is null)
        {
            return;
        }

        if (command.SourceUnitId is { } sourceUnitId
            && !await sources.UnitExistsAsync(sourceUnitId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.UnitNotFound, $"Одиниці {sourceUnitId} немає в довіднику.");
        }

        if (command.TargetUnitId is { } targetUnitId
            && !await sources.UnitExistsAsync(targetUnitId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                ErrorCodes.UnitNotFound, $"Одиниці {targetUnitId} немає в довіднику.");
        }

        map.SetUnits(command.SourceUnitId, command.TargetUnitId);
    }

    /// <summary>Складає DTO мапінгу для відповіді.</summary>
    private static EntityFieldMapDto Map(EntityFieldMap map) => EntityFieldMapLifecycle.Map(map);
}

/// <summary>Налаштування нового мапінгу, що приходять із форми конфігуратора.</summary>
/// <param name="SourceField">Поле або тег у джерелі.</param>
/// <param name="TargetKind">Куди лягає значення: колонка чи поле реєстру.</param>
/// <param name="TargetColumnDefId">Колонка-ціль; обов'язкове для <see cref="FieldTargetKind.Column"/>.</param>
/// <param name="TargetRegistryFieldDefId">
/// Поле реєстру-ціль; обов'язкове для <see cref="FieldTargetKind.RegistryField"/>.
/// </param>
/// <param name="SourceUnitId">Одиниця ДЖЕРЕЛА (ФВ-16.9); <c>null</c> — безрозмірне.</param>
/// <param name="TargetUnitId">Одиниця, у якій значення лягає в ECR; <c>null</c> — безрозмірне.</param>
/// <param name="TargetRowKey">
/// Рядок-адресат (<c>D-118</c>); <c>null</c> — мапінг не матеріалізується, точки
/// лишаються сирими для звірки.
/// </param>
/// <param name="Aggregation">Спосіб згортання точок періоду; обов'язковий разом із <paramref name="TargetRowKey"/>.</param>
public sealed record CreateEntityFieldMapCommand(
    string SourceField,
    FieldTargetKind TargetKind,
    int? TargetColumnDefId,
    int? TargetRegistryFieldDefId,
    int? SourceUnitId,
    int? TargetUnitId,
    string? TargetRowKey,
    AggregationKind? Aggregation);

/// <summary>Мапінг у відповіді на створення.</summary>
/// <param name="Id">Ідентифікатор запису <c>ext.EntityFieldMap</c>.</param>
/// <param name="SourceEntityId">Сутність джерела.</param>
/// <param name="SourceField">Поле в джерелі.</param>
/// <param name="TargetKind">Куди лягає значення.</param>
/// <param name="TargetColumnDefId">Колонка-ціль; <c>null</c> — мапінг на поле реєстру.</param>
/// <param name="TargetRegistryFieldDefId">Поле реєстру-ціль; <c>null</c> — мапінг на колонку.</param>
/// <param name="SourceUnitId">Одиниця джерела; <c>null</c> — безрозмірне.</param>
/// <param name="TargetUnitId">Одиниця цілі; <c>null</c> — безрозмірне.</param>
/// <param name="TargetRowKey">Рядок-адресат; <c>null</c> — не матеріалізується.</param>
/// <param name="Aggregation">Спосіб згортання точок періоду.</param>
/// <param name="IsActive">Чи діє мапінг.</param>
/// <param name="PendingSourceUnitChange">
/// Збір помітив іншу одиницю джерела і поставив мапінг на паузу; <c>null</c> — ні (ФВ-16.9).
/// </param>
public sealed record EntityFieldMapDto(
    int Id,
    int SourceEntityId,
    string SourceField,
    FieldTargetKind TargetKind,
    int? TargetColumnDefId,
    int? TargetRegistryFieldDefId,
    int? SourceUnitId,
    int? TargetUnitId,
    string? TargetRowKey,
    AggregationKind? Aggregation,
    bool IsActive,
    PendingSourceUnitChange? PendingSourceUnitChange);

/// <summary>Зміна одиниці джерела, що чекає рішення людини (ФВ-16.9).</summary>
/// <param name="ActualUnitCode">Одиниця, яку віддає джерело.</param>
/// <param name="ActualUnitId">Її id у довіднику; <c>null</c> — її спершу треба завести.</param>
/// <param name="DetectedAt">Коли збір це помітив.</param>
public sealed record PendingSourceUnitChange(string ActualUnitCode, int? ActualUnitId, DateTime DetectedAt)
{
    /// <summary>Позначка мапінгу; <c>null</c> — рішення не чекається.</summary>
    /// <param name="code">Код одиниці з позначки.</param>
    /// <param name="unitId">Id одиниці з позначки.</param>
    /// <param name="detectedAt">Час позначки.</param>
    public static PendingSourceUnitChange? From(string? code, int? unitId, DateTime? detectedAt)
        => code is null || detectedAt is not { } at ? null : new(code, unitId, at);
}
