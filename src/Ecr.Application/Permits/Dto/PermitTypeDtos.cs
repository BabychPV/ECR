// src/Ecr.Application/Permits/Dto/PermitTypeDtos.cs
namespace Ecr.Application.Permits.Dto;

/// <summary>Стовпець таблиці типу дозволу (і поле шапки) — у запиті й у відповіді (Permits, PM-03).</summary>
/// <param name="Code">Код стовпця в межах таблиці; після публікації не змінюється.</param>
/// <param name="NameL10n">Підпис мовами каталогу (en/ru/kz).</param>
/// <param name="DataType">Тип: <c>String</c>, <c>Int</c>, <c>Decimal</c>, <c>Bool</c>, <c>Date</c> або <c>Lookup</c>; після публікації не змінюється.</param>
/// <param name="UnitId">Одиниця виміру; <c>null</c> — безрозмірне.</param>
/// <param name="Required">Обов'язковість; ключовий стовпець завжди обов'язковий.</param>
/// <param name="Key">Чи входить у складений первинний ключ рядка (разом із дозволом).</param>
/// <param name="Group">Код групи стовпців для показу; <c>null</c> — поза групою.</param>
/// <param name="Precision">Загальна кількість цифр (лише <c>Decimal</c>).</param>
/// <param name="Scale">Цифр після коми (лише разом із точністю).</param>
/// <param name="ListScope">Лише для <c>Lookup</c>: <c>SharedRegistry</c>, <c>TypeOnly</c> або <c>SamePermitTable</c>.</param>
/// <param name="ListRegistryCode">Код спільного довідника (<c>SharedRegistry</c>) або прихованого реєстру списку (<c>TypeOnly</c>, у відповіді).</param>
/// <param name="SiblingTableCode">Код сестринської таблиці того самого типу (<c>SamePermitTable</c>).</param>
/// <param name="Choices">Значення списку <c>TypeOnly</c>, по одному підпису.</param>
/// <param name="Order">Позиція в таблиці.</param>
public sealed record PermitTypeColumnDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string DataType,
    int? UnitId,
    bool Required,
    bool Key,
    string? Group,
    byte? Precision,
    byte? Scale,
    string? ListScope,
    string? ListRegistryCode,
    string? SiblingTableCode,
    IReadOnlyList<string>? Choices,
    int Order);

/// <summary>Перевірка типу чи таблиці: вираз над стовпцями і рівень.</summary>
/// <param name="Code">Код перевірки (унікальний у межах реєстру).</param>
/// <param name="NameL10n">Назва й текст порушення мовами каталогу.</param>
/// <param name="Expression">Вираз діалекту правил довідників.</param>
/// <param name="Severity"><c>Warn</c> — попередження; <c>Block</c> — запис відхиляється.</param>
public sealed record PermitTypeRuleDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string Expression,
    string Severity);

/// <summary>Таблиця типу дозволу.</summary>
/// <param name="Code">Код таблиці в межах типу; після публікації не змінюється.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Order">Позиція в складі типу.</param>
/// <param name="Columns">Стовпці.</param>
/// <param name="Rules">Перевірки таблиці.</param>
public sealed record PermitTypeTableDto(
    string Code,
    Dictionary<string, string> NameL10n,
    int Order,
    IReadOnlyList<PermitTypeColumnDto> Columns,
    IReadOnlyList<PermitTypeRuleDto> Rules);

/// <summary>Короткий опис таблиці в переліку типів.</summary>
/// <param name="Code">Код таблиці.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="ColumnCount">Кількість стовпців.</param>
public sealed record PermitTypeTableSummaryDto(string Code, Dictionary<string, string> NameL10n, int ColumnCount);

/// <summary>Рядок переліку типів.</summary>
/// <param name="Code">Код типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category"><c>Air</c>, <c>Water</c>, <c>Waste</c> або <c>Other</c>.</param>
/// <param name="Status"><c>Draft</c> (ніколи не публікувався) або <c>Published</c>.</param>
/// <param name="Version">Версія: опублікована, а для ніколи не опублікованого типу — майбутня перша.</param>
/// <param name="HasUnpublishedChanges">Є чернетка, якої немає в опублікованому.</param>
/// <param name="Tables">Таблиці (з чернетки, якщо вона є).</param>
/// <param name="PermitCount">Скільки екземплярів дозволів (живих записів шапки).</param>
/// <param name="UpdatedAt">Остання зміна чернетки чи публікація, UTC.</param>
/// <param name="UpdatedByDisplayName">Хто змінив.</param>
public sealed record PermitTypeListItemDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string Category,
    string Status,
    int Version,
    bool HasUnpublishedChanges,
    IReadOnlyList<PermitTypeTableSummaryDto> Tables,
    int PermitCount,
    DateTime? UpdatedAt,
    string? UpdatedByDisplayName);

/// <summary>Повне визначення типу: опубліковане або чернетка, якщо вона є.</summary>
/// <param name="Code">Код типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category">Категорія.</param>
/// <param name="Status"><c>Draft</c> або <c>Published</c>.</param>
/// <param name="Version">Версія опублікованого визначення (або майбутня перша для <c>Draft</c>).</param>
/// <param name="DraftVersion">Версія, під якою вийде чернетка; <c>null</c>, якщо чернетки немає.</param>
/// <param name="HasUnpublishedChanges">Чи показане визначення — чернетка.</param>
/// <param name="RowVersion">Токен для наступного <c>PUT</c> чи публікації.</param>
/// <param name="HeaderFields">Поля шапки дозволу (фіксовані системою).</param>
/// <param name="Tables">Таблиці з усіма стовпцями.</param>
/// <param name="Rules">Перевірки рівня типу (над шапкою).</param>
/// <param name="PublishedAt">Момент останньої публікації, UTC.</param>
/// <param name="UpdatedAt">Остання зміна чернетки чи публікація, UTC.</param>
public sealed record PermitTypeDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string Category,
    string Status,
    int Version,
    int? DraftVersion,
    bool HasUnpublishedChanges,
    string RowVersion,
    IReadOnlyList<PermitTypeColumnDto> HeaderFields,
    IReadOnlyList<PermitTypeTableDto> Tables,
    IReadOnlyList<PermitTypeRuleDto> Rules,
    DateTime? PublishedAt,
    DateTime? UpdatedAt);

/// <summary>Запит на заведення типу.</summary>
/// <param name="Code">Код типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category"><c>Air</c>, <c>Water</c>, <c>Waste</c> або <c>Other</c>.</param>
/// <param name="CopyFromCode">Код типу, склад якого скопіювати в чернетку.</param>
public sealed record CreatePermitTypeRequest(
    string Code,
    Dictionary<string, string> NameL10n,
    string Category,
    string? CopyFromCode = null);

/// <summary>Запит на заміну чернетки визначення типу.</summary>
/// <param name="RowVersion">Токен, прочитаний разом з визначенням.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Category">Категорія.</param>
/// <param name="Tables">Повний склад таблиць після правки.</param>
/// <param name="Rules">Повний перелік перевірок типу.</param>
public sealed record ReplacePermitTypeRequest(
    string RowVersion,
    Dictionary<string, string> NameL10n,
    string Category,
    IReadOnlyList<PermitTypeTableDto> Tables,
    IReadOnlyList<PermitTypeRuleDto> Rules);

/// <summary>Запит на публікацію чернетки типу.</summary>
/// <param name="Reason">Причина зміни, не коротша за 10 символів: іде в журнал усіх реєстрів типу.</param>
/// <param name="RowVersion">Токен чернетки, яку публікують.</param>
public sealed record PublishPermitTypeRequest(string Reason, string RowVersion);

/// <summary>Вміст чернетки (зберігається JSON-ом у <c>cfg.PermitTypeDef.DraftJson</c>).</summary>
/// <param name="NameL10n">Назва.</param>
/// <param name="Category">Категорія.</param>
/// <param name="Tables">Таблиці.</param>
/// <param name="Rules">Перевірки типу.</param>
public sealed record PermitTypeContent(
    Dictionary<string, string> NameL10n,
    string Category,
    IReadOnlyList<PermitTypeTableDto> Tables,
    IReadOnlyList<PermitTypeRuleDto> Rules);
