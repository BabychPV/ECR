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
/// <param name="EntryCount">
/// Чинних записів сьогодні (UTC): активні, не видалені, у вікні дії. Лише в переліку; в інших
/// відповідях <c>null</c>.
/// </param>
/// <param name="DefinitionVersion">Версія опису довідника.</param>
/// <param name="DataChangedAt">Коли востаннє змінювалися записи, UTC; <c>null</c> — не змінювалися.</param>
/// <param name="UsedInColumns">
/// Скільки колонок шаблонів беруть значення з довідника. <c>null</c> без права
/// <c>Registry.EditDefinition</c> (те саме, що в <c>GET {code}/usage</c>): «не знаю» ≠ «ніде».
/// </param>
/// <param name="UsedInTemplates">У скількох шаблонах; <c>null</c> за тих самих умов.</param>
/// <param name="HasDraft">
/// Чи є незавершена чернетка опису; <c>null</c> без права <c>Registry.EditDefinition</c>.
/// </param>
public sealed record RegistryDefDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    bool IsHierarchical,
    bool IsTemporal,
    Domain.Enums.RegistrySourceKind SourceKind,
    IReadOnlyList<RegistryFieldDto> Fields,
    int? EntryCount = null,
    int DefinitionVersion = 0,
    DateTime? DataChangedAt = null,
    int? UsedInColumns = null,
    int? UsedInTemplates = null,
    bool? HasDraft = null);

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

/// <summary>
/// Заведення довідника з нуля — **без жодного поля**.
/// </summary>
/// <remarks>
/// ⛔ Поля заводяться окремою дією (<c>PUT …/{code}/definition</c>), і це не
/// зайвий крок: перше поле майже завжди ключове, а тип, обов'язковість і
/// зв'язок на інший довідник — рішення, які тут ще ніхто не ухвалив.
/// Склеїти обидві дії означало б вимагати від того, хто ще навіть не назвав
/// довідник, одразу описати його схему.
/// </remarks>
/// <param name="Code">Код, унікальний серед довідників.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="IsTemporal">
/// Чи мають майбутні записи вікно дії. Рішення приймається один раз: змінити
/// його для довідника з даними означало б перетлумачити вже введені записи.
/// </param>
public sealed record CreateRegistryDto(
    string Code,
    Dictionary<string, string> NameL10n,
    bool IsTemporal);
