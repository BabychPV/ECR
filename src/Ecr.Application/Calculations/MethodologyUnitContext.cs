// src/Ecr.Application/Calculations/MethodologyUnitContext.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Calculations;

/// <summary>
/// Одиниці версії методології — джерело для перевірки одиниць при публікації
/// (ФВ-16.6, ФВ-16.7).
/// </summary>
/// <remarks>
/// ⚠ Одиницю у виразі методології дають лише константи (<c>CST.x</c>,
/// <see cref="MethodologyConstant.UnitId"/>) і посилання на формулу
/// (<c>!Code</c>, оголошена <see cref="MethodologyFormula.OutputUnitId"/>).
/// Аргументи (<c>@x</c>) безрозмірні: їхня одиниця — у колонці таблиці, до якої
/// версію прив'язано, і таких таблиць може бути кілька з різними одиницями.
/// «Невідомо» тут означає «не перевіряється», а не «помилка».
/// </remarks>
public sealed class MethodologyUnitContext : IUnitContext
{
    private readonly Dictionary<string, int?> _constants = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int?> _formulas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, byte> _dimensions = [];
    private readonly UnitCatalogSnapshot _units;

    /// <summary>Складає джерело з констант і формул версії та довідника одиниць.</summary>
    /// <param name="constants">Усі константи версії, включно з рядками на речовину й дату.</param>
    /// <param name="formulas">Формули версії.</param>
    /// <param name="units">Довідник одиниць.</param>
    public MethodologyUnitContext(
        IReadOnlyList<MethodologyConstant> constants,
        IReadOnlyList<MethodologyFormula> formulas,
        UnitCatalogSnapshot units)
    {
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(units);

        _units = units;

        // ⚠ Константа з тим самим кодом буває кількома рядками (на речовину, на
        // вікно чинності). Якщо їхні одиниці розходяться, одиниця `CST.x` у
        // виразі залежить від рядка, і статично її не знає ніхто: тоді вона
        // невідома, а не «перша з них» — інакше відмова залежала б від порядку.
        foreach (var group in constants.GroupBy(c => c.Code, StringComparer.OrdinalIgnoreCase))
        {
            var distinct = group.Select(c => c.UnitId).Distinct().ToList();
            _constants[group.Key] = distinct.Count == 1 ? distinct[0] : null;
        }

        foreach (var formula in formulas)
        {
            _formulas[formula.Code] = formula.OutputUnitId;
        }

        foreach (var unit in units.Units.Values)
        {
            _dimensions[unit.Id] = unit.DimensionId;
        }
    }

    /// <inheritdoc />
    /// <remarks>Діалект методологій посилань на комірки не має (парсер їх відхиляє).</remarks>
    public int? GetReferenceUnit(CellReferenceNode reference) => null;

    /// <inheritdoc />
    public int? GetColumnUnit(int tableDefId, int columnDefId) => null;

    /// <inheritdoc />
    public int? GetConstantUnit(string code)
        => code is not null && _constants.TryGetValue(code, out var unit) ? unit : null;

    /// <inheritdoc />
    public int? GetFormulaUnit(string code)
        => code is not null && _formulas.TryGetValue(code, out var unit) ? unit : null;

    /// <inheritdoc />
    public byte GetDimension(int unitId) => _dimensions.TryGetValue(unitId, out var dimension) ? dimension : (byte)0;

    /// <inheritdoc />
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId)
        => _units.Derived.TryGetValue($"{numeratorUnitId}|{denominatorUnitId}", out var unit) ? unit : null;

    /// <inheritdoc />
    public int? ResolveUnitByCode(string code)
        => code is not null && _units.Units.TryGetValue(code, out var unit) ? unit.Id : null;

    /// <inheritdoc />
    public bool IsRowScopedUnit(CellReferenceNode reference) => false;
}
