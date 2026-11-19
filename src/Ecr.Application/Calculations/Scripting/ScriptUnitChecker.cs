// src/Ecr.Application/Calculations/Scripting/ScriptUnitChecker.cs
using System.Globalization;
using System.Runtime.CompilerServices;
using Ecr.Application.Ports;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.Scripting;

namespace Ecr.Application.Calculations.Scripting;

/// <summary>
/// Перевірка одиниць виразів <c>var</c>/<c>output</c> скрипта тим самим <see cref="UnitChecker"/>, що й формули
/// методологій (<see cref="MethodologyUnitChecks"/>): ті самі блокувальні ключі <c>expr.unit.*</c> і те саме правило
/// «результат в іншій розмірності, ніж оголошена одиниця» (MS, план 13.4).
/// </summary>
/// <remarks>
/// ⛔ Строгість рівно як у методологій, не більша: одиницю дають константи (<c>const k : unit</c>), оголошені чи
/// виведені величини й поля рядка. Поле <c>row.X</c> бере одиницю колонки в ЖИВІЙ таблиці (<c>rows from</c>); коли
/// таблиць кілька й вони розходяться, або в колонці одиниці немає, — вона «невідома», а не «безрозмірна» (FP-4).
/// Усе, чия одиниця невідома статично, — пошук у мапі, виклик своєї функції чи бібліотеки, агрегат, комірка іншої
/// таблиці, текст, заголовок, змінна без одиниці, у виразі якої є щось невідоме, — для перевірки «невідоме», а не
/// «безрозмірне»: воно ніколи не дає відмови. Тіла власних функцій перевіряються з параметрами невідомої одиниці
/// (виявляються лише внутрішні розбіжності: константи між собою). Чого немає й тут: відповідності одиниць
/// <c>visible var</c>-проміжних і виходів між версіями.
/// </remarks>
public static class ScriptUnitChecker
{
    /// <summary>Код розмірності «Безрозмірна» довідника <c>uom.Dimension</c> (одиниці <c>one</c>, <c>pct_vol</c>).</summary>
    public const string DimensionlessDimensionCode = "Dimensionless";

    /// <summary>Код діагностики блокувальної розбіжності одиниць.</summary>
    public const string Code = ScriptDiagnosticCodes.UnitMismatch;

    /// <summary>Перевіряє скрипт.</summary>
    /// <param name="script">Скомпільований скрипт.</param>
    /// <param name="catalog">Знімок довідника одиниць.</param>
    /// <param name="rowTables">
    /// Живі таблиці з <c>rows from</c>: з них поле <c>row.X</c> бере одиницю колонки; <c>null</c> — поля рядка
    /// без відомої одиниці (як було).
    /// </param>
    /// <returns>Діагностики <c>unitMismatch</c> із позицією оператора; порожньо — розбіжностей немає.</returns>
    public static IReadOnlyList<ScriptDiagnostic> Check(
        CompiledScript script, UnitCatalogSnapshot catalog, IReadOnlyList<LiveTable>? rowTables = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(catalog);

        var rowUnits = RowFieldUnits(script, catalog, rowTables);
        var diagnostics = new List<ScriptDiagnostic>();

        // Тіла власних функцій: параметри невідомої одиниці, змінних верхнього рівня функція не бачить.
        foreach (var function in script.Functions.Values)
        {
            var line = script.Members
                .FirstOrDefault(m => m.Kind == MemberKind.Function && string.Equals(m.Name, function.Name, StringComparison.Ordinal))
                ?.Line ?? 1;
            var inner = new State(script, catalog, rowUnits, function.Parameters);
            inner.Check(function.Name, function.Body, line);
            diagnostics.AddRange(inner.Diagnostics);
        }

        var state = new State(script, catalog, rowUnits, parameters: null);
        state.Walk(script.Statements);
        diagnostics.AddRange(state.Diagnostics);
        return diagnostics;
    }

    /// <summary>Одиниця поля рядка: однакова непорожня одиниця колонки в усіх живих таблицях, де колонка є.</summary>
    /// <remarks>
    /// ⛔ Одиниця безрозмірної розмірності (<c>one</c>) — відомо, що величина безрозмірна: значення <c>null</c> у словнику
    /// (як літерал). Інакше добуток «Sm3 × one» вважався б добутком двох розмірних величин («без оголошеної одиниці»),
    /// а далі «невідоме × відоме» брало б одиницю відомого операнда й давало хибне «інша розмірність».
    /// </remarks>
    private static Dictionary<string, int?> RowFieldUnits(CompiledScript script, UnitCatalogSnapshot catalog, IReadOnlyList<LiveTable>? rowTables)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        if (rowTables is null or { Count: 0 })
        {
            return result;
        }

        foreach (var field in script.RowFields.Where(f => !f.IsMonthly))
        {
            var units = new HashSet<int?>();
            foreach (var table in rowTables)
            {
                foreach (var column in table.Columns.Where(c => string.Equals(c.Code, field.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    units.Add(column.UnitId);
                }
            }

            if (units.Count == 1 && units.Single() is { } unit)
            {
                result[field.Name] = IsDimensionlessUnit(catalog, unit) ? null : unit;
            }
        }

        return result;
    }

    private static bool IsDimensionlessUnit(UnitCatalogSnapshot catalog, int unitId)
        => catalog.Units.Values.Any(u => u.Id == unitId
            && string.Equals(u.DimensionCode, DimensionlessDimensionCode, StringComparison.Ordinal));

    private sealed record VarInfo(int? Unit, bool Unknown);

    private sealed class State
    {
        private readonly UnitCatalogSnapshot _catalog;
        private readonly Dictionary<string, VarInfo> _vars = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int?> _consts = new(StringComparer.Ordinal);
        private readonly Context _context;
        private readonly UnitChecker _checker = new();
        private readonly IReadOnlyDictionary<string, int?> _rowUnits;
        private readonly HashSet<string> _parameters;

        public State(
            CompiledScript script,
            UnitCatalogSnapshot catalog,
            IReadOnlyDictionary<string, int?> rowUnits,
            IReadOnlyList<string>? parameters)
        {
            _catalog = catalog;
            _rowUnits = rowUnits;
            _parameters = new HashSet<string>(parameters ?? [], StringComparer.Ordinal);
            foreach (var (name, value) in script.Constants)
            {
                if (!value.IsMap)
                {
                    _consts[name] = NonDimensionlessUnitId(value.UnitCode);
                }
            }

            _context = new Context(this, catalog);
        }

        public List<ScriptDiagnostic> Diagnostics { get; } = [];

        public int? ConstUnit(string name) => _consts.TryGetValue(name, out var u) ? u : null;

        public int? FormulaUnit(string name)
            => _vars.TryGetValue(name, out var info) ? info.Unit : ConstUnit(name);

        public void Walk(IReadOnlyList<ScriptStatement> statements)
        {
            foreach (var statement in statements)
            {
                switch (statement)
                {
                    case VarStatement v:
                        Expression(v.Name, v.UnitCode, v.Expr, v.Line);
                        break;
                    case OutputStatement o:
                        Expression(o.Code, o.UnitCode, o.Expr, o.Line);
                        break;
                    case IfStatement i:
                        Walk(i.Then.Statements);
                        if (i.Else is not null)
                        {
                            Walk(i.Else.Statements);
                        }

                        break;
                    case ForeachSubstanceStatement f:
                        Walk(f.Body.Statements);
                        break;
                    default:
                        break;
                }
            }
        }

        /// <summary>Перевіряє вираз без оголошеної одиниці (тіло функції).</summary>
        public void Check(string name, AstNode expr, int line) => Expression(name, null, expr, line);

        private void Expression(string name, string? declaredCode, AstNode expr, int line)
        {
            var rewritten = Rewrite(expr);
            var found = new List<ExpressionDiagnostic>();
            var actual = _checker.Check(rewritten, _context, found);
            var declared = UnitId(declaredCode);

            // ⛔ Та сама третя клауза, що й в `MethodologyUnitChecks.RequireCompatibleUnits`: результат в іншій
            // РОЗМІРНОСТІ, ніж оголошена одиниця; одиниця тієї ж розмірності (т замість кг) не блокує.
            if (actual is { } a
                && declared is { } d
                && a != d
                && _context.GetDimension(a) is var da and not 0
                && _context.GetDimension(d) is var dd and not 0
                && da != dd)
            {
                found.Add(new ExpressionDiagnostic(
                    Ecr.Expressions.ExpressionErrors.UnitMismatch,
                    "The result has a different dimension than the declared unit.",
                    expr.Position,
                    1,
                    "expr.unit.dimensionMismatch"));
            }

            var blocking = found
                .Where(x => x.MessageKey is not null && MethodologyUnitChecks.BlockingKeys.Contains(x.MessageKey))
                .ToList();
            if (blocking.Count > 0)
            {
                var first = blocking[0];
                Diagnostics.Add(ScriptDiagnostic.At(
                    Code,
                    new ScriptSpan(0, 1, Math.Max(1, line), 1),
                    $"Incompatible units in '{name}': {first.Message}",
                    ("name", name),
                    ("reason", first.MessageKey!)));
            }

            // Що лишається відомим про величину для наступних виразів.
            var unit = declared ?? actual;
            var dimensionless = unit is { } u && IsDimensionlessUnit(_catalog, u);
            if (dimensionless)
            {
                unit = null;
            }

            _vars[name] = new VarInfo(unit, unit is null && !dimensionless && !IsCertainlyDimensionless(rewritten));
        }

        private int? UnitId(string? code)
            => code is not null && _catalog.Units.TryGetValue(code, out var unit) ? unit.Id : null;

        /// <summary>Одиниця константи; безрозмірна (<c>one</c>) і невказана — <c>null</c> (константа — як літерал).</summary>
        private int? NonDimensionlessUnitId(string? code)
            => UnitId(code) is { } id && !IsDimensionlessUnit(_catalog, id) ? id : null;

        private AstNode Rewrite(AstNode node)
        {
            // ⛔ L7-01: лівий гребінь ланцюга — рекурсія глибиною в кількість ланок.
            RuntimeHelpers.EnsureSufficientExecutionStack();

            switch (node)
            {
                case SymbolReferenceNode { Kind: SymbolKind.Formula } symbol when _parameters.Contains(symbol.Name):
                    // Параметр власної функції: одиниця невідома, навіть якщо ім'я збігається з константою.
                    return Unknown();

                case SymbolReferenceNode { Kind: SymbolKind.Argument } argument when _rowUnits.TryGetValue(argument.Name, out var rowUnit):
                {
                    // Поле рядка з одиницею колонки живої таблиці: синтетична величина з цією одиницею.
                    var key = "\u0001row." + argument.Name;
                    _vars[key] = new VarInfo(rowUnit, false);
                    return new SymbolReferenceNode(SymbolKind.Formula, key) { Position = argument.Position };
                }

                case SymbolReferenceNode { Kind: SymbolKind.Formula } symbol:
                    // Змінна з одиницею чи без невідомого — лишається символом; константа — теж.
                    if (_vars.TryGetValue(symbol.Name, out var info))
                    {
                        return info.Unit is null && info.Unknown ? Unknown() : symbol;
                    }

                    return _consts.ContainsKey(symbol.Name) ? symbol : Unknown();

                case SymbolReferenceNode { Kind: SymbolKind.Header or SymbolKind.Constant }:
                    return Unknown();

                case SymbolReferenceNode:
                    return node;

                case FunctionNode f when f.Name.StartsWith('$'):
                    // Хостові: пошук у мапі, власні функції, агрегати, комірки — одиниця невідома; `$entry` і
                    // `$periodEndKey` — число, безрозмірне.
                    return f.Name is "$entry" or "$periodEndKey"
                        ? new LiteralNode(0m, ExpressionValueType.Number) { Position = f.Position }
                        : Unknown();

                case FunctionNode f:
                    return f with { Arguments = [.. f.Arguments.Select(Rewrite)] };

                case BinaryNode b:
                    return b with { Left = Rewrite(b.Left), Right = Rewrite(b.Right) };

                case UnaryNode u:
                    return u with { Operand = Rewrite(u.Operand) };

                case ConditionalNode c:
                    return c with
                    {
                        Condition = Rewrite(c.Condition),
                        WhenTrue = Rewrite(c.WhenTrue),
                        WhenFalse = Rewrite(c.WhenFalse),
                    };

                default:
                    return node;
            }
        }

        // Маркер «невідомо»: аргумент без імені рядка — `UnitChecker` дає йому `null` і вважає невідомою одиницею.
        private static SymbolReferenceNode Unknown() => new(SymbolKind.Argument, "?");

        private bool IsCertainlyDimensionless(AstNode node)
        {
            // ⛔ L7-01: окремий від Rewrite рекурсивний спуск по тому самому піддереву.
            RuntimeHelpers.EnsureSufficientExecutionStack();
            return IsDimensionless(node);
        }

        private bool IsDimensionless(AstNode node)
            => node switch
            {
                LiteralNode or PeriodPropertyNode => true,
                SymbolReferenceNode { Kind: SymbolKind.Formula } s
                    => FormulaUnit(s.Name) is null
                       && (_vars.TryGetValue(s.Name, out var v) ? !v.Unknown : _consts.ContainsKey(s.Name)),
                SymbolReferenceNode => false,
                BinaryNode b => IsCertainlyDimensionless(b.Left) && IsCertainlyDimensionless(b.Right),
                UnaryNode u => IsCertainlyDimensionless(u.Operand),
                ConditionalNode c => IsCertainlyDimensionless(c.WhenTrue) && IsCertainlyDimensionless(c.WhenFalse),
                FunctionNode f when f.Name.Equals("CONVERT", StringComparison.OrdinalIgnoreCase) => false,
                FunctionNode f => f.Arguments.All(IsCertainlyDimensionless),
                _ => false,
            };
    }

    private sealed class Context(State state, UnitCatalogSnapshot catalog) : IUnitContext
    {
        private readonly Dictionary<int, byte> _dimensions = catalog.Units.Values.ToDictionary(u => u.Id, u => u.DimensionId);

        public int? GetReferenceUnit(CellReferenceNode reference) => null;

        public int? GetColumnUnit(int tableDefId, int columnDefId) => null;

        public int? GetConstantUnit(string code) => state.ConstUnit(code);

        public int? GetFormulaUnit(string code) => state.FormulaUnit(code);

        public byte GetDimension(int unitId) => _dimensions.TryGetValue(unitId, out var d) ? d : (byte)0;

        public int? FindDerived(int numeratorUnitId, int denominatorUnitId)
            => catalog.Derived.TryGetValue(
                string.Create(CultureInfo.InvariantCulture, $"{numeratorUnitId}|{denominatorUnitId}"), out var unit)
                ? unit
                : null;

        public int? ResolveUnitByCode(string code)
            => code is not null && catalog.Units.TryGetValue(code, out var unit) ? unit.Id : null;

        public bool IsRowScopedUnit(CellReferenceNode reference) => false;
    }
}
