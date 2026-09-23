// src/Ecr.Application/Documents/Dto/DocumentHeaderDto.cs
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.Dto;

/// <summary>Одне поле шапки документа разом із поточним значенням.</summary>
/// <param name="HeaderFieldDefId">Ідентифікатор поля у версії шаблону.</param>
/// <param name="Code">Код поля — адреса в <c>PATCH …/header</c>.</param>
/// <param name="Label">Підпис поля мовами каталогу.</param>
/// <param name="DataType">Тип даних поля.</param>
/// <param name="IsRequired">Обов'язковість заповнення.</param>
/// <param name="Value">
/// Поточне значення як «сире» значення CLR; <c>null</c> — поле не заповнене
/// (не розрізняє «ще не заповнили» і «явно стерли» — те саме спрощення,
/// що вже діє для <c>GET …/tables/{id}</c>).
/// </param>
/// <param name="LookupRegistryDefId">
/// Довідник поля — лише для <see cref="CellDataType.Lookup"/>, інакше
/// <c>null</c>. Той самий контракт, що <c>HeaderFieldDefDto</c>
/// (<c>GET …/template-versions/{id}/header-fields</c>): без цього поля
/// клієнт не може показати значення шапки повноцінним lookup-picker'ом,
/// як для Lookup-комірок сітки, — лише сире <c>ValueRegistryEntryId</c>.
/// ⚠ Людської назви обраного запису DTO НЕ несе: для звичайних
/// Lookup-комірок сітки (<c>RowDto.Cells</c>) такого поля теж немає —
/// клієнт резолвить назву сам через окремий виклик реєстру
/// (<c>LookupCellEditor</c> + <c>RegistryEntryDto[]</c>), тож вигадувати
/// новий формат саме тут означало б розійтися із симетрією.
/// </param>
public sealed record DocumentHeaderFieldDto(
    int HeaderFieldDefId,
    string Code,
    LocalizedText Label,
    CellDataType DataType,
    bool IsRequired,
    object? Value,
    int? LookupRegistryDefId = null);

/// <summary>Шапка документа: усі поля версії шаблону з поточними значеннями.</summary>
/// <param name="Fields">Поля в порядку <c>Ordinal</c>.</param>
public sealed record DocumentHeaderDto(IReadOnlyList<DocumentHeaderFieldDto> Fields);

/// <summary>Пакетна зміна шапки документа.</summary>
/// <param name="Fields">Зміни полів; поле, якого немає в списку, не чіпається.</param>
public sealed record PatchDocumentHeaderRequest(IReadOnlyList<PatchHeaderField> Fields);

/// <summary>
/// Зміна одного поля шапки. Той самий контракт, що <c>PatchCell</c> (R-B4):
/// <c>Value</c> заповнене — записати; <c>IsEmpty = true</c> — явна порожнеча;
/// обидва відсутні — поле в запиті помилкове (одне з двох обов'язкове).
/// </summary>
/// <param name="Code">Код поля.</param>
/// <param name="Value">Значення; ігнорується, коли <paramref name="IsEmpty"/> істинне.</param>
/// <param name="IsEmpty">Явна порожнеча.</param>
public sealed record PatchHeaderField(string Code, object? Value, bool IsEmpty = false);
