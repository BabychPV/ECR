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
    private readonly IReadOnlyDictionary<string, string> _formulaRegistries;

    /// <summary>Складає джерело з констант і формул версії та довідника одиниць.</summary>
    /// <param name="constants">Усі константи версії, включно з рядками на речовину й дату.</param>
    /// <param name="formulas">Формули версії.</param>
    /// <param name="units">Довідник одиниць.</param>
    /// <param name="registries">
    /// Форми довідників — одиниці полів <c>ROW.a.b</c>/<c>REGFIELD</c> і агрегатів
    /// (перевірка 20, RT-23b); <c>null</c> — версія довідників не читає.
    /// </param>
    /// <param name="formulaRegistries">
    /// Довідник запису, який дає формула (<c>!CASE</c>), — для <c>REGFIELD(!CASE, 'T_C')</c>.
    /// </param>
    public MethodologyUnitContext(
        IReadOnlyList<MethodologyConstant> constants,
        IReadOnlyList<MethodologyFormula> formulas,
        UnitCatalogSnapshot units,
        IRegistryShapeSource? registries = null,
        IReadOnlyDictionary<string, string>? formulaRegistries = null)
    {
        ArgumentNullException.ThrowIfNull(constants);
        ArgumentNullException.ThrowIfNull(formulas);
        ArgumentNullException.ThrowIfNull(units);

        _units = units;
        Registries = registries;
        _formulaRegistries = formulaRegistries ?? new Dictionary<string, string>();

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
    public IRegistryShapeSource? Registries { get; }

    /// <inheritdoc />
    public string? GetFormulaRegistry(string code)
        => code is not null && _formulaRegistries.TryGetValue(code, out var registry) ? registry : null;

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
