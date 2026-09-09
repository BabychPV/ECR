// src/Ecr.Application/Documents/Dto/TableSliceDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Зріз таблиці для grid. Порожні комірки не передаються — клієнт бере
/// <c>DefaultValue</c> з опису колонки (ФВ-3.8).
/// Бюджет усієї операції: p95 1.5 с на 500×60 (tz/08 §8.2).
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період екземпляра.</param>
/// <param name="Columns">Опис колонок таблиці.</param>
/// <param name="Rows">Рядки зі значеннями.</param>
/// <param name="CellPermissions">
/// Компактна мапа заборон: ключ — <c>"{rowKey}:{columnCode}"</c>, значення —
/// назва <see cref="Ecr.Domain.Enums.EditDenyReason"/>. Комірка, якої тут
/// немає, дозволена.
/// </param>
/// <param name="CellConfirmations">
/// Комірки, дозволені лише після ЯВНОГО підтвердження оператора
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>, <c>#43</c>). Ключ — той
/// самий формат, що й у <see cref="CellPermissions"/>
/// (<c>"{rowKey}:{columnCode}"</c>); значення — пояснення для діалогу
/// підтвердження. Комірка, якої тут немає, підтвердження не потребує.
/// </param>
public sealed record TableSliceDto(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows,
    IReadOnlyDictionary<string, string> CellPermissions,
    IReadOnlyDictionary<string, string> CellConfirmations);

/// <summary>Опис колонки для клієнта.</summary>
/// <remarks>
/// ⚠ <see cref="Scale"/> і <see cref="Precision"/> потрібні клієнтові не для
/// краси: за <c>ФВ-9.16c</c> вставка з Excel <b>округлює</b> зайві знаки і
/// показує це, а ручне введення — ні. Округлити на клієнті можна лише знаючи
/// масштаб колонки; без цих полів вставка з реального аркуша Excel
/// відхилялася б цілком (<c>ECR-CELL-0422</c>), тобто головний шлях введення
/// не працював би.
/// </remarks>
public sealed record ColumnDto(
    int Id,
    string Code,
    string Header,
    string DataType,
    int Ordinal,
    bool IsReadOnly,
    bool IsRequired,
    string? DisplayFormat,
    string? DefaultValue,
    int? LookupRegistryDefId,
    int? UnitId,
    string? UnitSymbol,
    byte? Precision = null,
    byte? Scale = null);

/// <summary>Рядок зі значеннями. Ключ у <paramref name="Cells"/> — код колонки.</summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="Ordinal">Позиція.</param>
/// <param name="RowKind">Режим рядків таблиці.</param>
/// <param name="Label">Підпис для фіксованих рядків.</param>
/// <param name="RowVersion">Версія для оптимістичного блокування.</param>
/// <param name="Cells">Значення; ключ — код колонки. Присутній ключ зі значенням
/// <c>null</c> означає <b>явну порожнечу</b>, відсутній ключ — «не заповнювали» (R-B4).</param>
/// <param name="IsOrphaned">
/// Рядок посилається на запис реєстру, що втратив чинність (ФВ-8.13).
/// ⚠ Читається зі збереженого поля <c>doc.TableRow.IsOrphaned</c>, а не
/// обчислюється при читанні: перерахунок на кожен зріз не вкладається в
/// бюджет 400 мс. Читання не блокує, <c>Submit</c> блокує.
/// </param>
public sealed record RowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string RowVersion,
    IReadOnlyDictionary<string, object?> Cells,
    bool IsOrphaned = false);
