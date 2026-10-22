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
/// <param name="UpdatedAt">
/// Коли востаннє збережено опис довідника, UTC; <c>null</c> — опис не змінювали після появи поля.
/// Лише в переліку.
/// </param>
/// <param name="UpdatedByDisplayName">
/// Відображуване ім'я (не логін) того, хто зберіг опис; <c>null</c> без права
/// <c>Registry.EditDefinition</c>, без запису або якщо користувача вже немає.
/// </param>
/// <param name="AsOfRequired">
/// Чи вимагає читання записів параметр <c>asOf</c>: довідник сам темпоральний або темпоральний його батько композиції. Лише в переліку.
/// </param>
/// <param name="DescriptionL10n">
/// Опис мовами каталогу, який показується під час вибору цілі Lookup. Зараз заповнений лише для груп типів дозволів
/// (<c>Kind = permitGroup</c>); для решти <c>null</c>.
/// </param>
/// <param name="Kind">
/// Вид цілі Lookup, який визначає СЕРВЕР (клієнт не розпізнає за префіксом коду): <c>Dictionary</c> - звичайний довідник,
/// <c>PermitHeader</c> - шапка типу дозволу (<c>PRM_&lt;TYPE&gt;</c>), <c>PermitGroup</c> - група типів дозволів (прихований
/// <c>PRMG_&lt;КОД&gt;</c>). Віддається і в переліку, і в <c>GET {code}/definition</c>.
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
    int? DefinitionVersion = null,
    DateTime? DataChangedAt = null,
    int? UsedInColumns = null,
    int? UsedInTemplates = null,
    bool? HasDraft = null,
    DateTime? UpdatedAt = null,
    string? UpdatedByDisplayName = null,
    bool? AsOfRequired = null,
    LocalizedText? DescriptionL10n = null,
    RegistryDefKind Kind = RegistryDefKind.Dictionary);

/// <summary>
/// Мінімальні відомості про довідник-ціль Lookup за ідентифікатором (<c>GET /registries/by-id/{id}</c>): для показу поточної цілі
/// колонки чи поля шапки, якої немає в переліку цілей («unlisted»).
/// </summary>
/// <param name="Id">Ідентифікатор довідника (той, що зберігає колонка/поле).</param>
/// <param name="Code">Код довідника.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="DescriptionL10n">Опис групи типів дозволів; для решти <c>null</c>.</param>
/// <param name="Kind"><c>Dictionary</c> / <c>PermitHeader</c> / <c>PermitGroup</c>.</param>
/// <param name="IsActive">Чи довідник (група) активний.</param>
public sealed record RegistryTargetDto(
    int Id,
    string Code,
    LocalizedText NameL10n,
    LocalizedText? DescriptionL10n,
    RegistryDefKind Kind,
    bool IsActive);

/// <summary>Вид довідника як цілі Lookup (<see cref="RegistryDefDto.Kind"/>).</summary>
public enum RegistryDefKind
{
    /// <summary>Звичайний довідник.</summary>
    Dictionary = 0,

    /// <summary>Реєстр шапки типу дозволу (<c>PRM_&lt;TYPE&gt;</c>).</summary>
    PermitHeader = 1,

    /// <summary>Група типів дозволів (прихований реєстр <c>PRMG_&lt;КОД&gt;</c>).</summary>
    PermitGroup = 2,
}

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
