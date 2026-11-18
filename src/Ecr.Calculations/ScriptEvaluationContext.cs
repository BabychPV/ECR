using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;
using Ecr.Scripting.Parsing;

namespace Ecr.Calculations;

/// <summary>
/// Контекст обчислення скрипта на ОДИН рядок (MS, план §3.5, §3.8, §4.4, §13): поля рядка, змінні, константи,
/// мапи, власні функції, бібліотеки, агрегати над таблицями-псевдонімами, місячні колонки.
/// </summary>
/// <remarks>
/// ⛔ Власної арифметики тут немає: усе, що рахує числа, — <see cref="Evaluator"/> (вузли AST, опущені
/// компілятором) і <see cref="IEvaluationArithmetic"/> режиму версії (агрегати). Контекст лише ВІДДАЄ значення за
/// іменем і виконує хостові функції на <c>$</c> (<see cref="IHostFunctionContext"/>).
/// <para>
/// ⚠ Таблиці-псевдоніми завантажені ДО обчислення рядка (<see cref="SetAliasRows"/>): хостова функція синхронна, а
/// читання таблиць — ні.
/// </para>
/// </remarks>
internal sealed class ScriptEvaluationContext : IHostFunctionContext, IBudgetedEvaluationContext
{
    /// <summary>Найбільша глибина викликів власних функцій (план §3.7).</summary>
    internal const int MaxCallDepth = 32;

    private readonly ScriptRuntime _runtime;
    private readonly Evaluator _evaluator;
    private readonly NumericPolicy _numeric;
    private readonly TraceRecorder _trace;
    private readonly IReadOnlyDictionary<string, CalculationArgument> _arguments;
    private readonly InstanceData _instance;
    private readonly CalculationInput _input;
    private readonly bool _entryRef;
    private readonly bool _legacyText;
    private readonly IRegistrySnapshot? _registries;

    private readonly List<Frame> _frames = [];
    private readonly List<Dictionary<string, ExpressionValue>> _scopes = [[]];
    private readonly List<Dictionary<(string Library, string Name), ExpressionValue>> _memos = [[]];
    private readonly HashSet<(string Library, string Name)> _resolving = [];
    private readonly Stack<SubstanceFrame> _substances = new();
    private readonly Dictionary<(CompiledScript Script, string Alias), IReadOnlyList<AliasRow>> _aliasRows = [];
    private readonly Dictionary<(CompiledScript Script, string Alias), AliasRow> _currentAlias = [];
    private readonly Dictionary<string, string?> _varUnits = new(StringComparer.Ordinal);
    private int _callDepth;

    internal ScriptEvaluationContext(
        ScriptRuntime runtime,
        Evaluator evaluator,
        NumericPolicy numeric,
        TraceRecorder trace,
        CalculationBindingContext binding,
        CalculationInput input,
        IReadOnlyDictionary<string, CalculationArgument> arguments,
        InstanceData instance)
    {
        _runtime = runtime;
        _evaluator = evaluator;
        _numeric = numeric;
        _trace = trace;
        _input = input;
        _arguments = arguments;
        _instance = instance;
        _registries = binding.Registries;
        Period = binding.Period;

        // ⛔ Правило плану §3.5: Strict, або скрипт читає довідники (знімок є) — Id запису; Legacy-числовий текст — лише Legacy.
        _entryRef = numeric.Mode == NumericMode.Strict || binding.Registries is not null;
        _legacyText = numeric.Mode == NumericMode.Legacy;
        _frames.Add(new Frame(runtime.Program.Main, null, null));
    }

    /// <inheritdoc />
    public EvaluationBudget? Budget { get; set; }

    /// <inheritdoc />
    public PeriodContext Period { get; }

    /// <inheritdoc />
    public IRegistrySnapshot? Registries => _registries;

    /// <inheritdoc />
    public string? CurrentSubstanceCode => _substances.TryPeek(out var top) ? top.Code : null;

    /// <summary>Таблиці-псевдоніми скрипта для поточного екземпляра.</summary>
    internal void SetAliasRows(CompiledScript script, string alias, IReadOnlyList<AliasRow> rows)
        => _aliasRows[(script, alias)] = rows;

    // ───── область видимості змінних ─────

    internal void PushScope() => _scopes.Add([]);

    internal void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    internal void SetVariable(string name, ExpressionValue value, string? unitCode)
    {
        _scopes[^1][name] = value;
        _varUnits[name] = unitCode;
    }

    /// <summary>Вхід в ітерацію <c>foreach substance</c>: нова область й нова пам'ять бібліотечних величин.</summary>
    internal void EnterSubstance(string variable, string code, long entryId)
    {
        _substances.Push(new SubstanceFrame(variable, code, entryId));
        _memos.Add([]);
        PushScope();
    }

    internal void LeaveSubstance()
    {
        PopScope();
        _memos.RemoveAt(_memos.Count - 1);
        _substances.Pop();
    }

    // ───── IEvaluationContext ─────

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
        => [ExpressionValue.Error(ExpressionErrors.BadReference)];

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId) => [];

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name) => ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue GetRegistryField(long registryEntryId, string fieldCode)
        => _registries is { } snapshot
            ? snapshot.GetField(registryEntryId, fieldCode)
            : ExpressionValue.Error(ExpressionErrors.BadReference);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
        => _runtime.Units.Convert(value, fromUnitCode, toUnitCode);

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name) => RowField(name, key: false);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name)
    {
        var frame = _frames[^1];

        if (frame.Parameters is not null && frame.Parameters.TryGetValue(name, out var parameter))
        {
            return parameter;
        }

        if (frame.Library is null)
        {
            for (var i = _scopes.Count - 1; i >= 0; i--)
            {
                if (_scopes[i].TryGetValue(name, out var variable))
                {
                    return variable;
                }
            }
        }
        else if (frame.Script.Statements.OfType<VarStatement>().FirstOrDefault(s => s.Name == name) is { } own)
        {
            return LibraryVariable(frame.Library, own);
        }
        else if (frame.Script.Params.Contains(name, StringComparer.Ordinal))
        {
            return ImporterVariable(name);
        }

        if (frame.Script.Constants.TryGetValue(name, out var constant))
        {
            return constant.Scalar ?? ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // `S.code` — код речовини поточної ітерації.
        if (name.EndsWith(".code", StringComparison.Ordinal))
        {
            var variableName = name[..^5];
            foreach (var substance in _substances)
            {
                if (substance.Variable == variableName)
                {
                    return ExpressionValue.Text(substance.Code);
                }
            }
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && FindLibrary(frame, name[..dot]) is { } library)
        {
            var member = name[(dot + 1)..];
            if (library.Script.Constants.TryGetValue(member, out var libraryConstant))
            {
                return libraryConstant.Scalar ?? ExpressionValue.Error(ExpressionErrors.BadValue);
            }

            if (library.Script.Statements.OfType<VarStatement>().FirstOrDefault(s => s.Name == member) is { } variable)
            {
                return LibraryVariable(library, variable);
            }
        }

        return ExpressionValue.Error(ExpressionErrors.BadReference);
    }

    /// <summary>
    /// Значення <c>param</c> бібліотеки (§3.8): змінна ІМПОРТЕРА з тим самим ім'ям, видима в місці обчислення
    /// (від найвужчої області: у <c>foreach substance</c> змінна блоку важливіша за верхню); немає — <c>null</c>.
    /// Для <c>Category</c> (як у старому рушії) бібліотека бачить категорію ПОТОЧНОЇ речовини, якщо в області є
    /// <c>SubstanceCategory</c>.
    /// </summary>
    private ExpressionValue ImporterVariable(string name)
    {
        if (name == "Category" && FindVariable("SubstanceCategory") is { } perSubstance)
        {
            return perSubstance;
        }

        return FindVariable(name) ?? ExpressionValue.Null;
    }

    private ExpressionValue? FindVariable(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out var variable))
            {
                return variable;
            }
        }

        return null;
    }

    private LibraryProgram? FindLibrary(Frame frame, string alias)
    {
        var libraries = frame.Library?.Imports ?? _runtime.Program.Libraries;
        return libraries.GetValueOrDefault(alias);
    }

    /// <summary>
    /// Ліниве обчислення величини бібліотеки в контексті рядка, що імпортує: результат запам'ятовується в межах
    /// поточної області (рядок або ітерація речовини), крок пишеться в трейс із джерелом (§3.8).
    /// </summary>
    private ExpressionValue LibraryVariable(LibraryProgram library, VarStatement statement)
    {
        var key = (library.Code, statement.Name);
        var memo = _memos[^1];
        if (memo.TryGetValue(key, out var known))
        {
            return known;
        }

        if (!_resolving.Add(key))
        {
            return ExpressionValue.Error(ExpressionErrors.RuntimeCycle);
        }

        _frames.Add(new Frame(library.Script, library, null));
        try
        {
            var result = Evaluate(statement.Expr, Budget);
            var stored = CalculationSteps.FinishStep(
                statement.Name,
                statement.SourceText,
                result,
                _numeric,
                _trace,
                visible: false,
                new TraceSource(library.Code, library.VersionId),
                (context: this, expression: statement.Expr, unit: statement.UnitCode),
                static state => state.context.Describe(state.expression, state.unit));
            memo[key] = stored;
            return stored;
        }
        finally
        {
            _frames.RemoveAt(_frames.Count - 1);
            _resolving.Remove(key);
        }
    }

    // ───── обчислення вузлів ─────

    /// <summary>Обчислює вузол Evaluator-ом із наданим бюджетом (або новим, якщо його немає).</summary>
    internal ExpressionValue Evaluate(AstNode node, EvaluationBudget? budget)
        => _evaluator.Evaluate(
            node, this, ExpressionDialect.Methodology, budget ?? new EvaluationBudget(Evaluator.MaxEvaluationSteps));

    // ───── поля рядка ─────

    private ExpressionValue RowField(string name, bool key)
    {
        var optional = _runtime.IsOptionalField(_frames[^1].Script, name);
        if (!_arguments.TryGetValue(name, out var argument))
        {
            return optional ? ExpressionValue.Null : ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);
        }

        var value = key
            ? CalculationSteps.ToValue(argument, entryRef: true, legacyNumericText: false)
            : CalculationSteps.ToValue(argument, _entryRef, _legacyText);

        return value.IsNull && !optional ? ExpressionValue.Error(ExpressionErrors.ArgumentNotFound) : value;
    }

    private static bool TryMonth(ExpressionValue value, out int month)
    {
        month = 0;
        if (value.AsNumber() is not { } number || number != decimal.Truncate(number) || number < 1 || number > 12)
        {
            return false;
        }

        month = (int)number;
        return true;
    }

    // ───── хостові функції ─────

    /// <inheritdoc />
    public bool TryInvokeHost(
        string name, IReadOnlyList<ExpressionValue> args, EvaluationBudget budget, out ExpressionValue result)
    {
        switch (name)
        {
            case "$lookup":
                result = Lookup(args);
                return true;

            case "$stripSpaces":
                result = StripSpaces(args);
                return true;

            case "$key":
                result = args.Count == 1 && args[0].Value is string field
                    ? RowField(field, key: true)
                    : ExpressionValue.Error(ExpressionErrors.BadValue);
                return true;

            case "$entry":
                result = args.Count == 2 && args[0].Value is string registry && args[1].Value is string entry
                    ? _runtime.EntryId(registry, entry) is { } id
                        ? ExpressionValue.Number(id)
                        : ExpressionValue.Error(ExpressionErrors.BadReference)
                    : ExpressionValue.Error(ExpressionErrors.BadValue);
                return true;

            case "$periodEndKey":
                result = ExpressionValue.Number((Period.End.Year * 10000) + (Period.End.Month * 100) + Period.End.Day);
                return true;

            case "$month":
                result = Month(args);
                return true;

            case "$cell":
                result = Cell(args);
                return true;

            case "$cellMonth":
                result = CellMonth(args);
                return true;

            case "$agg":
                result = args.Count == 1 && args[0].AsNumber() is { } index
                    ? Aggregate((int)index, budget)
                    : ExpressionValue.Error(ExpressionErrors.BadValue);
                return true;

            default:
                if (name.StartsWith("$call:", StringComparison.Ordinal))
                {
                    result = Call(name[6..], args, budget);
                    return true;
                }

                result = default;
                return false;
        }
    }

    private ExpressionValue Month(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count != 3 || args[0].Value is not string field)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        if (!TryMonth(args[1], out var month) || _instance.MonthColumn(field, month) is not { } column)
        {
            return ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);
        }

        var optional = args[2].Value is true;
        var value = _arguments.TryGetValue(column, out var argument)
            ? CalculationSteps.ToValue(argument, _entryRef, _legacyText)
            : ExpressionValue.Null;

        return value.IsNull && !optional ? ExpressionValue.Error(ExpressionErrors.ArgumentNotFound) : value;
    }

    private ExpressionValue Cell(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count != 2 || args[0].Value is not string alias || args[1].Value is not string column
            || !_currentAlias.TryGetValue((_frames[^1].Script, alias), out var row))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return CellValue(row, column);
    }

    private ExpressionValue CellMonth(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count != 3 || args[0].Value is not string alias || args[1].Value is not string column
            || !_currentAlias.TryGetValue((_frames[^1].Script, alias), out var row))
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return TryMonth(args[2], out var month) && row.Instance.MonthColumn(column, month) is { } code
            ? CellValue(row, code)
            : ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);
    }

    private ExpressionValue CellValue(AliasRow row, string column)
    {
        if (!row.Arguments.TryGetValue(column, out var argument))
        {
            // Колонки в таблиці немає взагалі — #ARG; є, але комірка порожня — null (агрегат її пропускає).
            return row.Instance.ColumnCodes.Contains(column)
                ? ExpressionValue.Null
                : ExpressionValue.Error(ExpressionErrors.ArgumentNotFound);
        }

        return CalculationSteps.ToValue(argument, _entryRef, _legacyText);
    }

    /// <summary>
    /// <c>StripSpaces(текст)</c>: текст без жодного пробільного символу (<c>char.IsWhiteSpace</c>) — так старий рушій
    /// зводить ключ категорії перед перевіркою «відомих категорій». Помилка й <c>null</c> проходять як є, не-текст
    /// лишається собою (тип результату правила категорії перевіряють далі).
    /// </summary>
    private static ExpressionValue StripSpaces(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count != 1)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var value = args[0];
        return value is { Type: ExpressionValueType.Text, Value: string text }
            ? ExpressionValue.Text(string.Concat(text.Where(ch => !char.IsWhiteSpace(ch))))
            : value;
    }

    private ExpressionValue Lookup(IReadOnlyList<ExpressionValue> args)
    {
        if (args.Count < 2 || args[0].Value is not string mapName)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var frame = _frames[^1];
        var constants = frame.Script.Constants;
        var key = mapName;
        var dot = mapName.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0 && !constants.ContainsKey(mapName) && FindLibrary(frame, mapName[..dot]) is { } library)
        {
            constants = library.Script.Constants;
            key = mapName[(dot + 1)..];
        }

        if (!constants.TryGetValue(key, out var current))
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        for (var i = 1; i < args.Count; i++)
        {
            var text = KeyText(args[i]);
            if (text is null || current.Map is null || !current.Map.TryGetValue(text, out var next))
            {
                return ExpressionValue.Error(ExpressionErrors.NotAvailable);
            }

            current = next;
        }

        return current.Scalar ?? ExpressionValue.Error(ExpressionErrors.NotAvailable);
    }

    private static string? KeyText(ExpressionValue value)
        => value.Type switch
        {
            ExpressionValueType.Text => (string)value.Value!,
            ExpressionValueType.Number => value.AsNumber() is { } number ? ScriptNumbers.KeyText(number) : null,
            _ => null,
        };

    private ExpressionValue Call(string name, IReadOnlyList<ExpressionValue> args, EvaluationBudget budget)
    {
        var frame = _frames[^1];
        ScriptFunction? function = null;
        LibraryProgram? library = frame.Library;
        var script = frame.Script;

        if (!script.Functions.TryGetValue(name, out function))
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && FindLibrary(frame, name[..dot]) is { } target
                && target.Script.Functions.TryGetValue(name[(dot + 1)..], out function))
            {
                library = target;
                script = target.Script;
            }
        }

        if (function is null || function.Parameters.Count != args.Count || _callDepth >= MaxCallDepth)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var parameters = new Dictionary<string, ExpressionValue>(function.Parameters.Count, StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            parameters[function.Parameters[i]] = args[i];
        }

        _frames.Add(new Frame(script, library, parameters));
        _callDepth++;
        try
        {
            return Evaluate(function.Body, budget);
        }
        finally
        {
            _callDepth--;
            _frames.RemoveAt(_frames.Count - 1);
        }
    }

    // ───── агрегати (§13.1) ─────

    private ExpressionValue Aggregate(int index, EvaluationBudget budget)
    {
        var script = _frames[^1].Script;
        if (index < 0 || index >= script.Aggregates.Count)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        var aggregate = script.Aggregates[index];
        var slot = (script, aggregate.TableAlias);
        if (!_aliasRows.TryGetValue(slot, out var rows))
        {
            return ExpressionValue.Error(ExpressionErrors.BadReference);
        }

        var arithmetic = _numeric.Arithmetic;
        var had = _currentAlias.TryGetValue(slot, out var outer);
        try
        {
            var count = 0;
            ExpressionValue? accumulated = null;
            var any = false;

            foreach (var row in rows)
            {
                _currentAlias[slot] = row;

                if (aggregate.Where is not null)
                {
                    var condition = Evaluate(aggregate.Where, budget);
                    if (condition.IsError)
                    {
                        return condition;
                    }

                    if (condition.Type != ExpressionValueType.Boolean || condition.Value is not true)
                    {
                        continue;
                    }
                }

                if (aggregate.Kind == AggregateKind.Count)
                {
                    count++;
                    continue;
                }

                if (aggregate.Kind == AggregateKind.Any)
                {
                    any = true;
                    break;
                }

                var value = Evaluate(aggregate.Value!, budget);
                if (value.IsError)
                {
                    return value;
                }

                if (value.IsNull)
                {
                    continue;
                }

                count++;
                switch (aggregate.Kind)
                {
                    case AggregateKind.First:
                        return value;

                    case AggregateKind.Sum or AggregateKind.Avg:
                        if (value.Type != ExpressionValueType.Number)
                        {
                            return ExpressionValue.Error(ExpressionErrors.BadValue);
                        }

                        accumulated = accumulated is { } sum ? arithmetic.Binary(sum, value, BinaryOperator.Add) : value;
                        if (accumulated.Value.IsError)
                        {
                            return accumulated.Value;
                        }

                        break;

                    default:
                        if (accumulated is not { } best)
                        {
                            accumulated = value;
                            break;
                        }

                        var order = Compare(best, value, arithmetic);
                        if (order is null)
                        {
                            return ExpressionValue.Error(ExpressionErrors.BadValue);
                        }

                        if (aggregate.Kind == AggregateKind.Min ? order > 0 : order < 0)
                        {
                            accumulated = value;
                        }

                        break;
                }
            }

            return aggregate.Kind switch
            {
                AggregateKind.Count => ExpressionValue.Number(count),
                AggregateKind.Any => ExpressionValue.Boolean(any),
                AggregateKind.Sum => accumulated ?? ExpressionValue.Number(0m),
                AggregateKind.Avg => accumulated is { } total
                    ? arithmetic.Binary(total, ExpressionValue.Number(count), BinaryOperator.Divide)
                    : ExpressionValue.Null,
                _ => accumulated ?? ExpressionValue.Null,
            };
        }
        finally
        {
            if (had)
            {
                _currentAlias[slot] = outer!;
            }
            else
            {
                _currentAlias.Remove(slot);
            }
        }
    }

    private static int? Compare(ExpressionValue left, ExpressionValue right, IEvaluationArithmetic arithmetic)
    {
        if (left.Type == ExpressionValueType.Number && right.Type == ExpressionValueType.Number)
        {
            return arithmetic.CompareNumbers(left, right);
        }

        return left.Type == ExpressionValueType.Text && right.Type == ExpressionValueType.Text
            ? string.CompareOrdinal((string)left.Value!, (string)right.Value!)
            : null;
    }

    // ───── деталі кроку для трейсу ─────

    /// <summary>
    /// Деталі кроку: імена, які читає вираз, з їхніми значеннями й одиницями (форма JSON та сама, що в формул).
    /// </summary>
    internal TraceDetail Describe(AstNode expression, string? unitCode)
    {
        var inputs = new List<TraceInput>();
        var seen = new HashSet<(TraceInputKind, string)>();
        var pending = new Stack<AstNode>();
        pending.Push(expression);

        while (pending.TryPop(out var node))
        {
            switch (node)
            {
                case SymbolReferenceNode { Kind: SymbolKind.Argument } symbol:
                    AddArgument(inputs, seen, symbol.Name, node);
                    break;

                case SymbolReferenceNode { Kind: SymbolKind.Formula } symbol when seen.Add((TraceInputKind.Formula, symbol.Name)):
                    var isConstant = _frames[^1].Script.Constants.ContainsKey(symbol.Name);
                    inputs.Add(new TraceInput(
                        isConstant ? TraceInputKind.Constant : TraceInputKind.Formula,
                        symbol.Name,
                        Format(Evaluate(node, new EvaluationBudget(Evaluator.MaxEvaluationSteps))),
                        isConstant
                            ? _frames[^1].Script.Constants[symbol.Name].UnitCode
                            : _varUnits.GetValueOrDefault(symbol.Name)));
                    break;

                case PeriodPropertyNode period when seen.Add((TraceInputKind.Period, period.Property)):
                    inputs.Add(new TraceInput(
                        TraceInputKind.Period,
                        period.Property,
                        Format(Evaluate(node, new EvaluationBudget(Evaluator.MaxEvaluationSteps))),
                        null));
                    break;

                case BinaryNode binary:
                    pending.Push(binary.Right);
                    pending.Push(binary.Left);
                    break;

                case UnaryNode unary:
                    pending.Push(unary.Operand);
                    break;

                case FunctionNode function:
                    if ((function.Name is "$key" or "$month") && function.Arguments.Count > 0
                        && function.Arguments[0] is LiteralNode { Value: string field })
                    {
                        AddArgument(inputs, seen, field, function);
                    }
                    else if (function.Name == "$lookup" && function.Arguments.Count > 0
                             && function.Arguments[0] is LiteralNode { Value: string map }
                             && seen.Add((TraceInputKind.Constant, map)))
                    {
                        inputs.Add(new TraceInput(TraceInputKind.Constant, map, Format(Evaluate(function, new EvaluationBudget(Evaluator.MaxEvaluationSteps))), null));
                    }

                    for (var i = function.Arguments.Count - 1; i >= 0; i--)
                    {
                        pending.Push(function.Arguments[i]);
                    }

                    break;

                default:
                    break;
            }
        }

        return new TraceDetail(unitCode, inputs);
    }

    private void AddArgument(List<TraceInput> inputs, HashSet<(TraceInputKind, string)> seen, string name, AstNode node)
    {
        if (!seen.Add((TraceInputKind.Argument, name)))
        {
            return;
        }

        _arguments.TryGetValue(name, out var argument);
        inputs.Add(new TraceInput(
            TraceInputKind.Argument,
            name,
            Format(Evaluate(node, new EvaluationBudget(Evaluator.MaxEvaluationSteps))),
            argument?.UnitId is { } unitId ? _runtime.UnitCodes.GetValueOrDefault(unitId) : null,
            new TraceCell(_input.TableInstanceId, _input.SourceRowKey, name)));
    }

    /// <summary>Значення для трейсу й живого перегляду.</summary>
    internal static string? Format(ExpressionValue value)
        => value.Type switch
        {
            ExpressionValueType.Null => null,
            ExpressionValueType.Error => value.ErrorCode,
            ExpressionValueType.Number => value.AsNumber() is { } number
                ? TraceJson.Format(number)
                : value.AsDouble()?.ToString("R", CultureInfo.InvariantCulture),
            ExpressionValueType.Boolean => (bool)value.Value! ? "true" : "false",
            ExpressionValueType.Date => ((DateTime)value.Value!).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => value.Value as string,
        };

    private sealed record Frame(CompiledScript Script, LibraryProgram? Library, Dictionary<string, ExpressionValue>? Parameters);

    private sealed record SubstanceFrame(string Variable, string Code, long EntryId);
}
