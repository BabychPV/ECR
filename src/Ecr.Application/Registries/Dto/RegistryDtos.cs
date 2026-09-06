using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries.Dto;

/// <summary>Опис довідника для конфігуратора і для клієнта.</summary>
/// <remarks>
/// Записи довідника живуть окремо в <see cref="RegistryEntryDto"/>: опис
/// довідника — метадані й змінюється рідко, записи — дані і можуть
/// обчислюватися сотнями.
/// </remarks>
/// <param name="Id">Ідентифікатор визначення.</param>
/// <param name="Code">Код довідника.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsHierarchical">Чи має записи-нащадки.</param>
/// <param name="SourceKind">
/// Хто master (<c>ФВ-8.9</c>). Потрібен тому, хто складає набір для
/// перемикання: без нього довідник, який уже в цільовому режимі, і той, який
/// ще ні, у переліку виглядають однаково.
/// </param>
/// <param name="IsTemporal">Чи мають записи вікно дії; від цього залежить обов'язковість <c>asOf</c>.</param>
/// <param name="Fields">Поля довідника.</param>
public sealed record RegistryDefDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    bool IsHierarchical,
    bool IsTemporal,
    Domain.Enums.RegistrySourceKind SourceKind,
    IReadOnlyList<RegistryFieldDto> Fields);

/// <summary>Поле довідника.</summary>
/// <param name="Id">Ідентифікатор поля.</param>
/// <param name="Code">Код поля.</param>
/// <param name="NameL10n">Підпис мовами каталогу.</param>
/// <param name="DataType">Тип значення.</param>
/// <param name="IsRequired">Обов'язковість.</param>
/// <param name="IsScopeField">
/// Чи можна звужувати доступ за цим полем. Саме ці поля бере
/// <c>RoleAssignment.ScopeJson</c>; решта для звуження недоступна.
/// </param>
/// <param name="LookupRegistryDefId">Довідник-джерело для полів-посилань.</param>
/// <param name="UnitId">Одиниця для числових полів; <c>null</c> — безрозмірне.</param>
public sealed record RegistryFieldDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    string DataType,
    bool IsRequired,
    bool IsScopeField,
    int? LookupRegistryDefId,
    int? UnitId);
