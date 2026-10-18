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
