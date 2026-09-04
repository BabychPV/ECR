// src/Ecr.Application/Documents/Dto/TableSliceDto.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Зріз таблиці для grid. Порожні комірки не передаються — клієнт бере
/// <c>DefaultValue</c> з опису колонки (ФВ-3.8).
/// Бюджет усієї операції: p95 1.5 с на 500×60 (tz/08 §8.2).
/// </summary>
public sealed record TableSliceDto(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows,
    IReadOnlyDictionary<string, string> CellPermissions);

/// <summary>Опис колонки для клієнта.</summary>
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
    string? UnitSymbol);

/// <summary>Рядок зі значеннями. Ключ у <paramref name="Cells"/> — код колонки.</summary>
public sealed record RowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string RowVersion,
    IReadOnlyDictionary<string, object?> Cells);
