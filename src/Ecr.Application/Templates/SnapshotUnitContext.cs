// src/Ecr.Application/Templates/SnapshotUnitContext.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Templates;

/// <summary>
/// Одиниці колонок версії шаблону — джерело для перевірки публікації
/// (ФВ-16.6).
/// </summary>
/// <remarks>
/// ⚠ До Етапу 4 перевірка одиниць не виконувалася взагалі: обробник передавав
/// <c>unitContext: null</c>, бо довідника <c>uom.Unit</c> ще не існувало. Це
/// було видно з виклику, а не сховане в значенні за замовчуванням — і саме
/// тому забути про неї було неможливо.
/// </remarks>
public sealed class SnapshotUnitContext(
    TemplateVersionSnapshot snapshot, UnitCatalogSnapshot units) : IUnitContext
{
    /// <inheritdoc />
    public int? GetReferenceUnit(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Column(reference)?.UnitId;
    }

    /// <inheritdoc />
    public int? GetColumnUnit(int tableDefId, int columnDefId)
        => snapshot.ColumnsById.TryGetValue(columnDefId, out var column) ? column.UnitId : null;

    /// <inheritdoc />
    /// <remarks>
    /// Констант методології у версії шаблону немає за побудовою: `CST.`
    /// належить діалекту методологій, і шаблон про них не знає (`Q-066`).
    /// </remarks>
    public int? GetConstantUnit(string code) => null;

    /// <inheritdoc />
    public byte GetDimension(int unitId) => units.DimensionOf(unitId);

    /// <inheritdoc />
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId)
        => units.Derived.TryGetValue($"{numeratorUnitId}|{denominatorUnitId}", out var unit)
            ? unit
            : null;

    /// <inheritdoc />
    public int? ResolveUnitByCode(string code)
        => code is not null && units.Units.TryGetValue(code, out var unit) ? unit.Id : null;

    /// <inheritdoc />
    /// <remarks>
    /// Колонка <c>DataType = Unit</c> тримає одиницю в КОЖНІЙ комірці
    /// (<c>doc.CellValue.ValueUnitId</c>, R-A4). Її <c>UnitId</c> порожній не
    /// тому, що вона безрозмірна, а тому, що одиниці в неї багато.
    /// </remarks>
    public bool IsRowScopedUnit(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Column(reference)?.DataType == CellDataType.Unit;
    }

    /// <summary>Колонка, на яку вказує посилання; <c>null</c> — не резолвиться.</summary>
    /// <remarks>
    /// Нерезолвлене посилання тут не є помилкою ОДИНИЦІ: про нього вже сказав
    /// резолвер, і другий раз казати те саме означало б подвоїти список
    /// проблем публікації.
    /// </remarks>
    private ColumnDef? Column(CellReferenceNode reference)
        => snapshot.Sheets
            .SelectMany(s => s.Tables)
            .Where(t => reference.TableCode is null
                        || string.Equals(t.Code, reference.TableCode, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.Columns)
            .FirstOrDefault(c => !c.IsDeleted
                                 && string.Equals(
                                     c.Code, reference.ColumnSelector, StringComparison.OrdinalIgnoreCase));
}
