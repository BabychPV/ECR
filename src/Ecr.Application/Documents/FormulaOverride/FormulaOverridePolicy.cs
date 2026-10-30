using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;

namespace Ecr.Application.Documents.FormulaOverride;

/// <summary>
/// ✎ D-461: які комірки таблиці рахують формули шаблону (колонки й комірки) і які з цих формул дозволяють ручне
/// значення («Дозволити ручне значення», <see cref="FormulaDef.AllowManualOverride"/>). Чиста функція від знімка версії.
/// </summary>
/// <remarks>
/// ⚠ Лише області <see cref="FormulaScope.Column"/> і <see cref="FormulaScope.Cell"/>: саме їхні комірки сьогодні
/// відхиляються як <c>CalculatedCell</c> (обчислювана колонка; комірочна формула WW-02). Цілі формули РЯДКА в
/// колонках уводу і далі редагуються як звичайні (поведінка до D-461 не змінюється — «решта без змін»); прапорець
/// формули рядка зберігається, але на редагування не впливає (відкрите питання в D-461).
/// <para>
/// ⚠ Одна комірка — не більше однієї формули: перетини цілей відхиляє публікація (<c>FormulaTargetConflicts</c>).
/// Тому «формула комірки» визначена однозначно; комірочна формула перевіряється першою.
/// </para>
/// </remarks>
public sealed class FormulaOverridePolicy
{
    private static readonly IReadOnlyDictionary<int, FormulaDef> NoColumns = new Dictionary<int, FormulaDef>();
    private static readonly IReadOnlyDictionary<(string, int), FormulaDef> NoCells =
        new Dictionary<(string, int), FormulaDef>();

    /// <summary>Таблиця без формул колонки/комірки.</summary>
    public static readonly FormulaOverridePolicy None = new(NoColumns, NoCells);

    private readonly IReadOnlyDictionary<int, FormulaDef> _columns;
    private readonly IReadOnlyDictionary<(string RowKey, int ColumnDefId), FormulaDef> _cells;

    private FormulaOverridePolicy(
        IReadOnlyDictionary<int, FormulaDef> columns,
        IReadOnlyDictionary<(string RowKey, int ColumnDefId), FormulaDef> cells)
    {
        _columns = columns;
        _cells = cells;
    }

    /// <summary>У таблиці є хоч одна формула колонки чи комірки.</summary>
    public bool HasTargets => _columns.Count > 0 || _cells.Count > 0;

    /// <summary>Хоч одна формула таблиці дозволяє ручне значення.</summary>
    public bool AnyAllows => _columns.Values.Any(f => f.AllowManualOverride) || _cells.Values.Any(f => f.AllowManualOverride);

    /// <summary>Політика таблиці зі знімка версії.</summary>
    /// <param name="table">Таблиця знімка (з формулами й рядками).</param>
    public static FormulaOverridePolicy For(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);

        Dictionary<int, FormulaDef>? columns = null;
        Dictionary<(string, int), FormulaDef>? cells = null;
        Dictionary<int, string>? rowKeys = null;

        foreach (var formula in table.Formulas)
        {
            if (formula.IsDeleted || formula.ColumnDefId is not { } columnId)
            {
                continue;
            }

            switch (formula.Scope)
            {
                case FormulaScope.Column:
                    (columns ??= [])[columnId] = formula;
                    break;

                case FormulaScope.Cell when formula.RowDefId is { } rowDefId:
                    rowKeys ??= table.Rows.Where(r => !r.IsDeleted).ToDictionary(r => r.Id, r => r.RowKeyValue);
                    if (rowKeys.TryGetValue(rowDefId, out var rowKey))
                    {
                        (cells ??= [])[(rowKey, columnId)] = formula;
                    }

                    break;

                default:
                    break;
            }
        }

        return columns is null && cells is null
            ? None
            : new FormulaOverridePolicy(
                (IReadOnlyDictionary<int, FormulaDef>?)columns ?? NoColumns,
                (IReadOnlyDictionary<(string, int), FormulaDef>?)cells ?? NoCells);
    }

    /// <summary>
    /// Формула, що рахує комірку (рядок × колонка); <c>null</c> — комірку не рахує формула колонки чи комірки.
    /// </summary>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <param name="column">Колонка.</param>
    /// <remarks>
    /// ⚠ Формула КОЛОНКИ діє лише на обчислюваній колонці (<see cref="ColumnDef.IsComputed"/>): саме вона дає
    /// <c>CalculatedCell</c>. Формула колонки на колонці вводу (давні версії) лишається як була — поза D-461.
    /// </remarks>
    public FormulaDef? FormulaOf(string rowKey, ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (_cells.Count > 0 && _cells.TryGetValue((rowKey, column.Id), out var cell))
        {
            return cell;
        }

        return column.IsComputed && _columns.TryGetValue(column.Id, out var byColumn) ? byColumn : null;
    }

    /// <summary>Формула колонки (область <see cref="FormulaScope.Column"/>); <c>null</c> — немає.</summary>
    public FormulaDef? ColumnFormula(int columnDefId)
        => _columns.TryGetValue(columnDefId, out var formula) ? formula : null;

    /// <summary>Комірки комірочних формул, що дозволяють ручне значення: <c>(RowKey, ColumnDefId)</c>.</summary>
    public IEnumerable<(string RowKey, int ColumnDefId)> AllowingCells
        => _cells.Where(p => p.Value.AllowManualOverride).Select(p => p.Key);

    /// <summary>Чи дозволяє формула цієї комірки ручне значення ЗАРАЗ (прапорець увімкнено).</summary>
    public bool Allows(string rowKey, ColumnDef column) => FormulaOf(rowKey, column)?.AllowManualOverride == true;
}
