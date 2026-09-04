// src/Ecr.Domain/ValueObjects/CellAddress.cs
namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Адреса комірки: період, рядок, колонка. Позиційних координат не існує —
/// це і є головна відмінність від чинного рішення.
/// </summary>
/// <param name="PeriodKey">Ключ періоду.</param>
/// <param name="TableRowId">Ідентифікатор рядка (<c>doc.TableRow.Id</c>).</param>
/// <param name="ColumnDefId">Ідентифікатор колонки (<c>cfg.ColumnDef.Id</c>).</param>
public readonly record struct CellAddress(PeriodKey PeriodKey, long TableRowId, int ColumnDefId);
