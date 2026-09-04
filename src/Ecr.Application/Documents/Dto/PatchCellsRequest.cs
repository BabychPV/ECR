// src/Ecr.Application/Documents/Dto/PatchCellsRequest.cs
namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Пакетна зміна комірок. Часткове застосування заборонене: конфлікт у
/// будь-якому рядку відхиляє весь батч (B04 §2.3).
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="Origin">Джерело зміни: <c>UserEdit</c>, <c>Import</c>, <c>Recalculation</c>.</param>
/// <param name="Rows">Рядки зі змінами.</param>
public sealed record PatchCellsRequest(
    long TableInstanceId,
    int PeriodKey,
    string Origin,
    IReadOnlyList<PatchRow> Rows);

/// <summary>
/// Рядок у пакетній зміні.
/// </summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="BaseVersion">
/// Версія рядка, від якої відштовхується клієнт (hex <c>rowversion</c>).
/// <c>null</c> означає <b>створення</b> нового рядка (R-B2).
/// </param>
/// <param name="Cells">Зміни комірок.</param>
public sealed record PatchRow(
    string RowKey,
    string? BaseVersion,
    IReadOnlyList<PatchCell> Cells);

/// <summary>
/// Зміна однієї комірки. Три різні операції (R-B4):
/// значення — записати; <c>Value = null</c> — стерти (рядок видаляється);
/// <c>IsEmpty = true</c> — явна порожнеча; поле відсутнє в запиті — не чіпати.
/// </summary>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="Value">Значення; <c>null</c> = стерти.</param>
/// <param name="IsEmpty">Явна порожнеча.</param>
public sealed record PatchCell(
    string ColumnCode,
    object? Value,
    bool IsEmpty = false);
