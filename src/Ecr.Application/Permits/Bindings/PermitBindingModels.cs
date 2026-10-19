// src/Ecr.Application/Permits/Bindings/PermitBindingModels.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Permits.Bindings;

/// <summary>Колонка таблиці аркуша для перевірки прив'язки.</summary>
/// <param name="Id">Колонка.</param>
/// <param name="Code">Код.</param>
/// <param name="DataType">Тип.</param>
/// <param name="UnitId">Одиниця.</param>
/// <param name="Precision">Точність.</param>
/// <param name="Scale">Масштаб.</param>
/// <param name="IsReadOnly">Чи колонка лише для машинного запису.</param>
/// <param name="LookupRegistryDefId">Довідник <c>Lookup</c>.</param>
public sealed record SheetColumnInfo(
    int Id, string Code, CellDataType DataType, int? UnitId, byte? Precision, byte? Scale, bool IsReadOnly, int? LookupRegistryDefId);

/// <summary>Таблиця аркуша версії шаблону для перевірки прив'язки.</summary>
/// <param name="Id">Таблиця.</param>
/// <param name="Code">Код.</param>
/// <param name="RowMode">Режим рядків.</param>
/// <param name="MaxDynamicRows">Стеля динамічних рядків; <c>null</c> - без стелі.</param>
/// <param name="Columns">Колонки (без видалених).</param>
public sealed record SheetTableInfo(
    int Id, string Code, TableRowMode RowMode, int? MaxDynamicRows, IReadOnlyList<SheetColumnInfo> Columns);

/// <summary>Поле таблиці дозволу (поле прихованого реєстру <c>PRM_&lt;тип&gt;_&lt;таблиця&gt;</c> без поля композиції).</summary>
/// <param name="Code">Код поля.</param>
/// <param name="DataType">Тип.</param>
/// <param name="UnitId">Одиниця.</param>
/// <param name="Precision">Точність (з надбудови поля).</param>
/// <param name="Scale">Масштаб (з надбудови поля).</param>
/// <param name="LookupRegistryDefId">Довідник <c>Lookup</c>.</param>
public sealed record PermitFieldInfo(
    string Code, CellDataType DataType, int? UnitId, byte? Precision, byte? Scale, int? LookupRegistryDefId);

/// <summary>Поле шапки версії шаблону.</summary>
/// <param name="Id">Поле.</param>
/// <param name="Code">Код.</param>
/// <param name="DataType">Тип.</param>
/// <param name="LookupRegistryDefId">Довідник <c>Lookup</c>.</param>
public sealed record HeaderFieldInfo(int Id, string Code, CellDataType DataType, int? LookupRegistryDefId);

/// <summary>Колонка прив'язки з запиту.</summary>
/// <param name="Role">Роль.</param>
/// <param name="PermitFieldCode">Поле дозволу.</param>
/// <param name="SheetColumnDefId">Колонка аркуша.</param>
public sealed record BindingColumnInput(PermitColumnRole Role, string PermitFieldCode, int SheetColumnDefId);

/// <summary>Прив'язка з запиту.</summary>
/// <param name="SheetTableDefId">Таблиця аркуша.</param>
/// <param name="Mode">Режим.</param>
/// <param name="SelectionRule">Правило вибору дозволу.</param>
/// <param name="HeaderFieldDefId">Поле шапки (лише для <c>FromHeaderLookup</c>).</param>
/// <param name="Columns">Зіставлення колонок.</param>
public sealed record PermitBindingInput(
    int SheetTableDefId,
    PermitBindingMode Mode,
    PermitSelectionRule SelectionRule,
    int? HeaderFieldDefId,
    IReadOnlyList<BindingColumnInput> Columns);

/// <summary>Факти, проти яких перевіряється прив'язка.</summary>
/// <param name="Sheet">Таблиця аркуша у ВЕРСІЇ; <c>null</c> - у версії такої таблиці немає.</param>
/// <param name="HeaderFields">Поля шапки версії.</param>
/// <param name="TypePublished">Чи тип дозволу опублікований (до того реєстри таблиць порожні).</param>
/// <param name="PermitFields">Поля таблиці типу дозволу.</param>
/// <param name="PermitRowCount">Скільки рядків у найбільшому чинному дозволі; <c>null</c> - невідомо.</param>
public sealed record PermitBindingContext(
    SheetTableInfo? Sheet,
    IReadOnlyList<HeaderFieldInfo> HeaderFields,
    bool TypePublished,
    IReadOnlyList<PermitFieldInfo> PermitFields,
    int? PermitRowCount);

/// <summary>Зауваження до прив'язки: ключ каталогу, аргументи, текст.</summary>
/// <param name="MessageKey">Ключ каталогу повним літералом (<c>err.ECR-PRM-0422.binding…</c> для помилки).</param>
/// <param name="Args">Аргументи підстановок.</param>
/// <param name="Text">Пояснення для журналу й попереджень.</param>
public sealed record PermitBindingIssue(string MessageKey, IReadOnlyDictionary<string, string> Args, string Text)
{
    /// <summary>Короткий випадок - частина ключа після останньої крапки.</summary>
    public string Case => MessageKey[(MessageKey.LastIndexOf('.') + 1)..];
}

/// <summary>Результат перевірки прив'язки.</summary>
/// <param name="Errors">Помилки: зберігати не можна.</param>
/// <param name="Warnings">Попередження: зберегти можна.</param>
public sealed record PermitBindingValidation(
    IReadOnlyList<PermitBindingIssue> Errors, IReadOnlyList<PermitBindingIssue> Warnings)
{
    /// <summary>Помилок немає.</summary>
    public bool IsValid => Errors.Count == 0;
}
