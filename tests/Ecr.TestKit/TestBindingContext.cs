using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;

namespace Ecr.TestKit;

/// <summary>
/// Типи й одиниці колонок для перевірок публікації — таблиця замість знімка.
/// </summary>
/// <remarks>
/// Ключем служить КОД колонки, а не ідентифікатор: тест пише вираз так само,
/// як його напише конфігуратор, і не мусить наперед роздавати номери.
/// </remarks>
public sealed class TestBindingContext : ITypeContext, IUnitContext
{
    /// <summary>Типи колонок за кодом.</summary>
    public Dictionary<string, ExpressionValueType> ColumnTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Одиниці колонок за кодом.</summary>
    public Dictionary<string, int> ColumnUnits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Одиниці констант методології.</summary>
    public Dictionary<string, int> ConstantUnits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Типи аргументів методології.</summary>
    public Dictionary<string, ExpressionValueType> ArgumentTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Розмірність кожної одиниці.</summary>
    public Dictionary<int, byte> Dimensions { get; } = [];

    /// <summary>Похідні одиниці: <c>чисельник|знаменник</c> → одиниця.</summary>
    public Dictionary<string, int> Derived { get; } = new(StringComparer.Ordinal);

    /// <summary>Одиниці за кодом — те, що резолвить <c>CONVERT</c>.</summary>
    public Dictionary<string, int> UnitsByCode { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Колонки, у яких одиниця задається на рядок (ФВ-16.8).</summary>
    public HashSet<string> RowScopedUnitColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Тип колонки, якої немає в таблиці типів.</summary>
    public ExpressionValueType DefaultColumnType { get; set; } = ExpressionValueType.Number;

    /// <summary>Форми довідників (RT-21); <c>null</c> — перевірки, яким потрібна форма, мовчать.</summary>
    public IRegistryShapeSource? Registries { get; set; }

    /// <summary>Довідник правила — для <c>THIS</c> і <c>ROW.</c> верхнього рівня.</summary>
    public string? RuleRegistryCode { get; set; }

    /// <summary>Цілі <c>Lookup</c>-колонок за кодом колонки.</summary>
    public Dictionary<string, string> ColumnRegistries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Цілі <c>Lookup</c>-аргументів методології за іменем.</summary>
    public Dictionary<string, string> ArgumentRegistries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Довідники записів, які дають інші формули (<c>!CASE</c>), за кодом формули.</summary>
    public Dictionary<string, string> FormulaRegistries { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public string? GetReferenceRegistry(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ColumnRegistries.GetValueOrDefault(reference.ColumnSelector);
    }

    /// <inheritdoc />
    public string? GetArgumentRegistry(string name) => ArgumentRegistries.GetValueOrDefault(name);

    /// <inheritdoc />
    public string? GetFormulaRegistry(string code) => FormulaRegistries.GetValueOrDefault(code);

    /// <inheritdoc />
    public ExpressionValueType GetReferenceType(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ColumnTypes.GetValueOrDefault(reference.ColumnSelector, DefaultColumnType);
    }

    /// <inheritdoc />
    public ExpressionValueType GetColumnType(int tableDefId, int columnDefId) => DefaultColumnType;

    /// <inheritdoc />
    public ExpressionValueType GetArgumentType(string name)
        => ArgumentTypes.GetValueOrDefault(name, ExpressionValueType.Number);

    /// <inheritdoc />
    public int? GetReferenceUnit(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return ColumnUnits.TryGetValue(reference.ColumnSelector, out var unit) ? unit : null;
    }

    /// <inheritdoc />
    public int? GetColumnUnit(int tableDefId, int columnDefId) => null;

    /// <inheritdoc />
    public int? GetConstantUnit(string code)
        => ConstantUnits.TryGetValue(code, out var unit) ? unit : null;

    /// <inheritdoc />
    public byte GetDimension(int unitId) => Dimensions.GetValueOrDefault(unitId, (byte)0);

    /// <inheritdoc />
    public int? FindDerived(int numeratorUnitId, int denominatorUnitId)
        => Derived.TryGetValue($"{numeratorUnitId}|{denominatorUnitId}", out var unit) ? unit : null;

    /// <inheritdoc />
    public int? ResolveUnitByCode(string code)
        => code is not null && UnitsByCode.TryGetValue(code, out var unit) ? unit : null;

    /// <inheritdoc />
    public bool IsRowScopedUnit(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return RowScopedUnitColumns.Contains(reference.ColumnSelector);
    }
}
