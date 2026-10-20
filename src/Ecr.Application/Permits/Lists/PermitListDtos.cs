// src/Ecr.Application/Permits/Lists/PermitListDtos.cs
namespace Ecr.Application.Permits.Lists;

/// <summary>Стовпець типу дозволу, що бере значення зі списку.</summary>
/// <param name="TypeCode">Код типу.</param>
/// <param name="TypeNameL10n">Назва типу мовами каталогу.</param>
/// <param name="TableCode">Код таблиці типу.</param>
/// <param name="TableNameL10n">Назва таблиці.</param>
/// <param name="FieldCode">Код стовпця.</param>
/// <param name="FieldNameL10n">Назва стовпця.</param>
public sealed record PermitListUseDto(
    string TypeCode,
    Dictionary<string, string> TypeNameL10n,
    string TableCode,
    Dictionary<string, string> TableNameL10n,
    string FieldCode,
    Dictionary<string, string> FieldNameL10n);

/// <summary>Список вибору: спільний довідник, список «лише для типу» або таблиця того самого дозволу.</summary>
/// <param name="Key">Ключ списку для маршруту: <c>shared:{registryCode}</c>, <c>type:{typeCode}:{tableCode}:{fieldCode}</c> або <c>permit:{typeCode}:{tableCode}:{fieldCode}</c>.</param>
/// <param name="Scope"><c>SharedRegistry</c>, <c>TypeOnly</c> або <c>SamePermitTable</c>.</param>
/// <param name="RegistryCode">Код довідника зі значеннями (для <c>SharedRegistry</c> і <c>TypeOnly</c> - прихованого); для таблиці дозволу <c>null</c>.</param>
/// <param name="TypeCode">Тип-власник для <c>TypeOnly</c> і <c>SamePermitTable</c>; для спільного <c>null</c>.</param>
/// <param name="NameL10n">Назва: довідника (спільний) або стовпця-власника (решта).</param>
/// <param name="ItemCount">Скільки чинних значень; <c>null</c> для таблиці того самого дозволу (значення - рядки дозволу).</param>
/// <param name="Editable">Чи може поточний користувач змінювати значення (право і область).</param>
/// <param name="UsedBy">Стовпці опублікованих типів, що беруть значення зі списку.</param>
public sealed record PermitListDto(
    string Key,
    string Scope,
    string? RegistryCode,
    string? TypeCode,
    Dictionary<string, string> NameL10n,
    int? ItemCount,
    bool Editable,
    IReadOnlyList<PermitListUseDto> UsedBy);

/// <summary>Значення списку.</summary>
/// <param name="Id">Ідентифікатор запису довідника.</param>
/// <param name="Code">Код запису.</param>
/// <param name="DisplayL10n">Підпис мовами каталогу.</param>
public sealed record PermitListItemDto(long Id, string Code, Dictionary<string, string> DisplayL10n);

/// <summary>Список зі значеннями.</summary>
/// <param name="List">Опис списку.</param>
/// <param name="Items">Чинні значення в порядку показу.</param>
public sealed record PermitListDetailDto(PermitListDto List, IReadOnlyList<PermitListItemDto> Items);

/// <summary>Одна зміна значень списку.</summary>
/// <param name="Action"><c>Add</c>, <c>Update</c> або <c>Remove</c>.</param>
/// <param name="Id">Запис (для <c>Update</c> і <c>Remove</c>).</param>
/// <param name="DisplayL10n">Підпис (для <c>Add</c> і <c>Update</c>); для списку «лише для типу» обов'язкова мова <c>en</c>.</param>
public sealed record PermitListItemChange(string Action, long? Id, Dictionary<string, string>? DisplayL10n);

/// <summary>Запит на зміну значень списку: усе або нічого.</summary>
/// <param name="Changes">Зміни в порядку виконання (1..200).</param>
public sealed record ChangePermitListItemsRequest(IReadOnlyList<PermitListItemChange> Changes);
