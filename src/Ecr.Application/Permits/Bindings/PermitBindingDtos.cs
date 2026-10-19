// src/Ecr.Application/Permits/Bindings/PermitBindingDtos.cs
namespace Ecr.Application.Permits.Bindings;

/// <summary>Зіставлена колонка прив'язки у відповіді.</summary>
/// <param name="Role"><c>RowKey</c>, <c>Limit</c> або <c>Info</c>.</param>
/// <param name="PermitFieldCode">Код поля таблиці дозволу.</param>
/// <param name="SheetColumnDefId">Колонка аркуша.</param>
/// <param name="SheetColumnCode">Код колонки аркуша.</param>
/// <param name="Ordinal">Позиція в прив'язці.</param>
public sealed record PermitBindingColumnDto(
    string Role, string PermitFieldCode, int SheetColumnDefId, string SheetColumnCode, int Ordinal);

/// <summary>Прив'язка таблиці аркуша версії шаблону до таблиці типу дозволу.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код прив'язки (унікальний у системі).</param>
/// <param name="SheetTableDefId">Таблиця аркуша.</param>
/// <param name="SheetTableCode">Код таблиці аркуша.</param>
/// <param name="PermitTypeCode">Код типу дозволу.</param>
/// <param name="PermitTableCode">Код таблиці типу дозволу.</param>
/// <param name="Mode"><c>Rows</c> або <c>Formulas</c>.</param>
/// <param name="SelectionRule"><c>ByFacilityPeriodEnd</c> або <c>FromHeaderLookup</c>.</param>
/// <param name="HeaderFieldDefId">Поле шапки (лише для <c>FromHeaderLookup</c>).</param>
/// <param name="HeaderFieldCode">Код поля шапки.</param>
/// <param name="IsActive">Чи діє прив'язка.</param>
/// <param name="RowVersion">Токен зміни (base64).</param>
/// <param name="Columns">Зіставлення колонок у порядку позицій.</param>
public sealed record PermitBindingDto(
    int Id,
    string Code,
    int SheetTableDefId,
    string SheetTableCode,
    string PermitTypeCode,
    string PermitTableCode,
    string Mode,
    string SelectionRule,
    int? HeaderFieldDefId,
    string? HeaderFieldCode,
    bool IsActive,
    string RowVersion,
    IReadOnlyList<PermitBindingColumnDto> Columns);

/// <summary>Прив'язки версії разом зі станом версії.</summary>
/// <param name="IsEditable">Чи можна правити: <c>false</c> для опублікованої (замороженої) версії.</param>
/// <param name="Items">Прив'язки в порядку коду; порожній перелік - норма (механізм опційний).</param>
public sealed record PermitBindingsDto(bool IsEditable, IReadOnlyList<PermitBindingDto> Items);

/// <summary>Дозвіл, обраний для перегляду.</summary>
/// <param name="EntryId">Запис-шапка; <c>null</c> - дозволу немає.</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="ValidFrom">Початок чинності.</param>
/// <param name="ValidTo">Кінець чинності (виключно).</param>
/// <param name="ReasonIfNone">Чому дозволу немає: <c>noFacility</c>, <c>noPermit</c>, <c>headerEmpty</c>, <c>headerUnknown</c>.</param>
public sealed record PermitPreviewPermitDto(
    long? EntryId, string? Number, DateOnly? ValidFrom, DateOnly? ValidTo, string? ReasonIfNone);

/// <summary>Колонка аркуша в перегляді.</summary>
/// <param name="ColumnCode">Код колонки аркуша.</param>
/// <param name="Header">Заголовок (мова за замовчуванням).</param>
/// <param name="Role"><c>RowKey</c>, <c>Limit</c>, <c>Info</c> - з дозволу; <c>Fact</c> - вводить користувач; <c>Formula</c> - рахується.</param>
/// <param name="Locked">Колонку пише машина: користувач її не править.</param>
/// <param name="DataType">Тип даних колонки.</param>
/// <param name="Unit">Код одиниці; <c>null</c> - безрозмірна.</param>
public sealed record PermitPreviewColumnDto(
    string ColumnCode, string Header, string Role, bool Locked, string DataType, string? Unit);

/// <summary>Рядок аркуша в перегляді.</summary>
/// <param name="RowKey">Ключ рядка: <c>P&lt;id запису таблиці дозволу&gt;</c>.</param>
/// <param name="Cells">Значення за кодом колонки: з дозволу для зіставлених, з документа для фактичних (якщо документ задано).</param>
/// <param name="Locked">Колонки, які пише машина.</param>
public sealed record PermitPreviewRowDto(
    string RowKey, IReadOnlyDictionary<string, object?> Cells, IReadOnlyList<string> Locked);

/// <summary>Попередження перегляду.</summary>
/// <param name="Code">Стабільний код.</param>
/// <param name="Text">Пояснення.</param>
public sealed record PermitPreviewWarningDto(string Code, string Text);

/// <summary>
/// Перегляд аркуша очима користувача: що створить прив'язка для обраного дозволу (Permits, PM-09).
/// </summary>
/// <param name="Binding">Прив'язка.</param>
/// <param name="Permit">Обраний дозвіл.</param>
/// <param name="Columns">Усі колонки таблиці аркуша в порядку позицій.</param>
/// <param name="Rows">Заплановані рядки; у режимі <c>Formulas</c> порожні.</param>
/// <param name="Warnings">Попередження.</param>
public sealed record PermitBindingPreviewDto(
    PermitBindingDto Binding,
    PermitPreviewPermitDto Permit,
    IReadOnlyList<PermitPreviewColumnDto> Columns,
    IReadOnlyList<PermitPreviewRowDto> Rows,
    IReadOnlyList<PermitPreviewWarningDto> Warnings);

/// <summary>Зіставлена колонка в запиті.</summary>
/// <param name="Role"><c>RowKey</c>, <c>Limit</c> або <c>Info</c>.</param>
/// <param name="PermitFieldCode">Код поля таблиці дозволу.</param>
/// <param name="SheetColumnDefId">Колонка аркуша цієї версії.</param>
public sealed record PermitBindingColumnRequest(string Role, string PermitFieldCode, int SheetColumnDefId);

/// <summary>Запит на створення чи заміну прив'язки (<c>PUT</c> за кодом).</summary>
/// <param name="SheetTableDefId">Таблиця аркуша цієї версії.</param>
/// <param name="PermitTypeCode">Код опублікованого типу дозволу.</param>
/// <param name="PermitTableCode">Код таблиці типу дозволу.</param>
/// <param name="Mode"><c>Rows</c> або <c>Formulas</c>.</param>
/// <param name="SelectionRule"><c>ByFacilityPeriodEnd</c> або <c>FromHeaderLookup</c>.</param>
/// <param name="HeaderFieldDefId">Поле шапки - лише для <c>FromHeaderLookup</c>.</param>
/// <param name="IsActive">Чи діє прив'язка.</param>
/// <param name="Columns">Зіставлення колонок; у режимі <c>Formulas</c> порожнє.</param>
public sealed record SavePermitBindingRequest(
    int SheetTableDefId,
    string PermitTypeCode,
    string PermitTableCode,
    string Mode,
    string SelectionRule,
    int? HeaderFieldDefId,
    bool IsActive,
    IReadOnlyList<PermitBindingColumnRequest> Columns);
