using System.Globalization;
using System.Runtime.CompilerServices;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Scripting.Parsing;
using Ecr.Scripting.Syntax;

namespace Ecr.Scripting.Binding;

/// <summary>
/// Зв'язування: області видимості (§3.4), діагностики (§3.9, §13.7), опускання виразів у AST (§3.5, §13)
/// і збір метаданих <see cref="CompiledScript"/>.
/// </summary>
internal sealed class ScriptBinder
{
    private static readonly HashSet<string> RegistryFunctions = new(StringComparer.Ordinal)
    {
        "REGFIND", "REGONE", "REGFIELD", "REGSUM", "REGAVG", "REGMIN", "REGMAX", "REGCOUNT",
    };

    private readonly string text;
    private readonly ScriptCompileOptions options;
    private readonly ProgramSyntax program;
    private readonly List<ScriptDiagnostic> diagnostics = [];

    private readonly Dictionary<string, string> globalKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScriptConst> consts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSyntax> functionSyntax = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScriptFunction> functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompiledScript> imports = new(StringComparer.Ordinal);
    private readonly List<string> importList = [];
    private readonly Dictionary<string, TableRef> tables = new(StringComparer.Ordinal);
    private readonly List<string> paramNames = [];
    private readonly HashSet<string> topLevelNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> callGraph = new(StringComparer.Ordinal);

    private readonly List<RowsClause> rows = [];
    private readonly List<OutputInfo> outputs = [];
    private readonly Dictionary<string, OutputRecord> outputRegistry = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VisibleInfo> visibles = [];
    private readonly List<string> substances = [];
    private readonly List<RowFieldBuilder> rowFields = [];
    private readonly List<string> matchColumns = [];
    private readonly List<RegistryEntryRef> registryEntries = [];
    private readonly List<string> registryCodes = [];
    private readonly List<UnitUse> units = [];
    private readonly List<MemberInfo> members = [];
    private readonly List<AggregateInfo> aggregates = [];
    private readonly Dictionary<string, List<string>> tableColumns = new(StringComparer.Ordinal);
    private readonly List<ScriptStatement> topStatements = [];
    private readonly Stack<AggregateContext> aggregateStack = new();
    private readonly List<(int IfId, int Branch)> ifPath = [];

    private Scope scope = new(null);
    private bool needsRegistries;
    private bool inFunction;
    private string? currentFunction;
    private string? foreachVar;
    private bool skipAllowed;
    private int ifCounter;

    private ScriptBinder(string text, ScriptCompileOptions options, ProgramSyntax program)
    {
        this.text = text;
        this.options = options;
        this.program = program;
    }

    private enum Sym : byte { Var, Output, Param, Substance }

    private sealed class Scope(Scope? parent)
    {
        public Scope? Parent { get; } = parent;

        public Dictionary<string, Sym> Names { get; } = new(StringComparer.Ordinal);
    }

    private sealed class OutputRecord(OutputInfo info, List<(int, int)> path, bool topLevel)
    {
        public OutputInfo Info { get; } = info;

        public List<(int IfId, int Branch)> Path { get; } = path;

        public bool TopLevel { get; } = topLevel;
    }

    private sealed class RowFieldBuilder(string name, ScriptSpan span)
    {
        public string Name { get; } = name;

        public ScriptSpan Span { get; } = span;

        public bool AnyRequired { get; set; }

        public bool Monthly { get; set; }
    }

    private sealed class AggregateContext
    {
        public string? Alias { get; set; }

        public HashSet<string> Referenced { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Nested { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct Accessor(string? Member, IReadOnlyList<Expr>? Keys, ScriptSpan Span);

    public static (CompiledScript Script, List<ScriptDiagnostic> Diagnostics) Bind(
        string text, ScriptCompileOptions options, ProgramSyntax program)
    {
        var binder = new ScriptBinder(text, options, program);
        return binder.Run();
    }

    // ───── допоміжне ─────

    private void Report(string code, ScriptSpan span, string message, params (string, string)[] pairs)
        => diagnostics.Add(ScriptDiagnostic.At(code, span, message, pairs));

    private static LiteralNode Null() => new LiteralNode(null, ExpressionValueType.Null);

    private static T At<T>(T node, ScriptSpan span)
        where T : AstNode
        => node with { Position = span.Start };

    private static LiteralNode Lit(string value) => new(value, ExpressionValueType.Text);

    private static LiteralNode Lit(decimal value) => new(value, ExpressionValueType.Number);

    private string Slice(ScriptSpan span)
        => span.Start >= 0 && span.Start + span.Length <= text.Length ? text.Substring(span.Start, span.Length) : string.Empty;

    private void AddUnit(UnitRef unit) => units.Add(new UnitUse(unit.Code, unit.Span.Line, unit.Span.Column, unit.Span.Length));

    private void AddRegistryCode(string code)
    {
        if (!registryCodes.Contains(code, StringComparer.Ordinal))
        {
            registryCodes.Add(code);
        }
    }

    private void RegisterGlobal(string name, string kind, ScriptSpan span)
    {
        if (globalKinds.TryGetValue(name, out var existing))
        {
            Report(ScriptDiagnosticCodes.DuplicateName, span,
                $"The name '{name}' is already used ({existing}); names are unique regardless of case.",
                ("name", name), ("kind", existing));
            return;
        }

        globalKinds[name] = kind;
    }

    private bool IsVisibleName(string name)
    {
        for (var s = scope; s is not null; s = s.Parent)
        {
            if (s.Names.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return globalKinds.TryGetValue(name, out var kind) && kind is "const" or "function" or "import" or "table";
    }

    private void CheckBlockName(string name, ScriptSpan span)
    {
        if (IsVisibleName(name))
        {
            Report(ScriptDiagnosticCodes.DuplicateName, span,
                $"The name '{name}' is already visible here; a block name must not repeat an outer name.", ("name", name));
        }
    }

    // ───── запуск ─────

    private (CompiledScript, List<ScriptDiagnostic>) Run()
    {
        PreDeclare();

        foreach (var item in program.Items)
        {
            switch (item)
            {
                case RowsSyntax r:
                    BindRows(r);
                    break;
                case FunctionSyntax f:
                    BindFunction(f);
                    break;
                case ParamDeclSyntax pd:
                    BindParam(pd);
                    break;
                case StatementSyntax s:
                    topStatements.AddRange(BindStatements([s], topLevel: true));
                    break;
                default:
                    break; // const, table — розібрано наперед
            }
        }

        CheckRecursion();

        if (!options.IsLibrary && !options.IsModuleLevel && rows.Count == 0)
        {
            Report(ScriptDiagnosticCodes.MissingRows, new ScriptSpan(0, 0, 1, 1),
                "A script must contain at least one 'rows from …;' clause.");
        }

        return (BuildResult(), diagnostics);
    }

    private void BindParam(ParamDeclSyntax pd)
    {
        if (!options.IsLibrary)
        {
            Report(ScriptDiagnosticCodes.ParamOutsideLibrary, pd.Span,
                $"'param {pd.Name};' is allowed only in a library.", ("name", pd.Name));
            return;
        }

        // Ім'я видиме далі як змінна верхнього рівня; значення бере рушій у змінної імпортера (§3.8).
        scope.Names[pd.Name] = Sym.Var;
        paramNames.Add(pd.Name);
    }

    private CompiledScript BuildResult()
    {
        var tableColumnMap = tableColumns.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<string>)[.. kv.Value], StringComparer.Ordinal);
        var readTables = tables.Values.Select(t => t.TableCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var codes = registryCodes.ToList();
        var needs = needsRegistries;
        foreach (var lib in imports.Values)
        {
            needs |= lib.NeedsRegistries;
            foreach (var code in lib.RegistryCodes.Where(c => !codes.Contains(c, StringComparer.Ordinal)))
            {
                codes.Add(code);
            }
        }

        return new CompiledScript
        {
            IsLibrary = options.IsLibrary,
            Imports = importList,
            Params = paramNames,
            Rows = rows,
            Outputs = outputs,
            Visibles = visibles,
            Substances = substances,
            RowFields = [.. rowFields.Select(f => new RowFieldInfo(f.Name, !f.AnyRequired, f.Monthly) { Span = f.Span })],
            MatchColumns = matchColumns,
            RegistryEntries = registryEntries,
            RegistryCodes = codes,
            NeedsRegistries = needs,
            Units = units,
            Members = members,
            Statements = topStatements,
            StepDependencies = ComputeStepDependencies(),
            Constants = consts,
            Functions = functions,
            Tables = tables,
            ReadTables = readTables,
            TableColumns = tableColumnMap,
            Aggregates = aggregates,
        };
    }

    /// <summary>
    /// Залежності кроків верхнього рівня: які <c>var</c>/<c>output</c> верхнього рівня читає вираз кожного з них
    /// (прямо, через аргументи власних функцій і через агрегати).
    /// </summary>
    private Dictionary<string, IReadOnlyList<string>> ComputeStepDependencies()
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var statement in topStatements)
        {
            var (name, expression) = statement switch
            {
                VarStatement v => (v.Name, v.Expr),
                OutputStatement o => (o.Code, o.Expr),
                _ => (null, null),
            };
            if (name is null || expression is null)
            {
                continue;
            }

            var found = new List<string>();
            CollectStepReferences(expression, found, new HashSet<string>(StringComparer.Ordinal));
            result[name] = [.. found.Where(n => n != name).Distinct(StringComparer.Ordinal)];
        }

        return result;
    }

    private void CollectStepReferences(AstNode node, List<string> found, HashSet<string> visitedFunctions)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();

        switch (node)
        {
            case SymbolReferenceNode { Kind: SymbolKind.Formula } symbol:
                if (topLevelNames.Contains(symbol.Name))
                {
                    found.Add(symbol.Name);
                }

                break;
            case BinaryNode b:
                CollectStepReferences(b.Left, found, visitedFunctions);
                CollectStepReferences(b.Right, found, visitedFunctions);
                break;
            case UnaryNode u:
                CollectStepReferences(u.Operand, found, visitedFunctions);
                break;
            case ConditionalNode c:
                CollectStepReferences(c.Condition, found, visitedFunctions);
                CollectStepReferences(c.WhenTrue, found, visitedFunctions);
                CollectStepReferences(c.WhenFalse, found, visitedFunctions);
                break;
            case FunctionNode f:
                foreach (var argument in f.Arguments)
                {
                    CollectStepReferences(argument, found, visitedFunctions);
                }

                if (f.Name.StartsWith("$call:", StringComparison.Ordinal)
                    && functions.TryGetValue(f.Name["$call:".Length..], out var function)
                    && visitedFunctions.Add(function.Name))
                {
                    // Тіло функції бачить лише параметри, тож верхніх імен там немає; обхід лишає контракт чесним.
                    var inner = new List<string>();
                    CollectStepReferences(function.Body, inner, visitedFunctions);
                    found.AddRange(inner.Where(n => !function.Parameters.Contains(n, StringComparer.Ordinal)));
                }

                if (f.Name == "$agg" && f.Arguments is [LiteralNode { Value: decimal index }]
                    && (int)index >= 0 && (int)index < aggregates.Count)
                {
                    var aggregate = aggregates[(int)index];
                    if (aggregate.Value is { } value)
                    {
                        CollectStepReferences(value, found, visitedFunctions);
                    }

                    if (aggregate.Where is { } where)
                    {
                        CollectStepReferences(where, found, visitedFunctions);
                    }
                }

                break;
            default:
                break;
        }
    }

    private void PreDeclare()
    {
        foreach (var import in program.Imports)
        {
            RegisterGlobal(import.Name, "import", import.Span);
            if (importList.Contains(import.Name, StringComparer.Ordinal))
            {
                continue;
            }

            var library = options.ResolveImport?.Invoke(import.Name);
            if (library is null)
            {
                Report(ScriptDiagnosticCodes.UnknownImport, import.Span,
                    $"Library '{import.Name}' was not found.", ("name", import.Name));
                continue;
            }

            importList.Add(import.Name);
            imports[import.Name] = library;
        }

        foreach (var item in program.Items)
        {
            switch (item)
            {
                case ConstSyntax c:
                    RegisterGlobal(c.Name, "const", c.NameSpan);
                    if (c.Unit is { } cu)
                    {
                        AddUnit(cu);
                    }

                    var value = BuildConst(c.Value, c.Unit?.Code);
                    consts[c.Name] = value;
                    members.Add(new MemberInfo(c.Name, MemberKind.Const, c.NameSpan.Line, c.Unit?.Code, 0, value.IsMap));
                    break;
                case FunctionSyntax f:
                    RegisterGlobal(f.Name, "function", f.NameSpan);
                    functionSyntax[f.Name] = f;
                    members.Add(new MemberInfo(f.Name, MemberKind.Function, f.NameSpan.Line, null, f.Params.Count));
                    break;
                case TableSyntax t:
                    RegisterGlobal(t.Alias, "table", t.AliasSpan);
                    tables[t.Alias] = t.Table;
                    break;
                case VarSyntax v:
                    RegisterGlobal(v.Name, "var", v.NameSpan);
                    topLevelNames.Add(v.Name);
                    break;
                case ParamDeclSyntax pd:
                    RegisterGlobal(pd.Name, "param", pd.NameSpan);
                    topLevelNames.Add(pd.Name);
                    break;
                case OutputSyntax o:
                    RegisterGlobal(o.Code, "output", o.CodeSpan);
                    topLevelNames.Add(o.Code);
                    break;
                default:
                    break;
            }
        }
    }

    private ScriptConst BuildConst(ConstValueSyntax syntax, string? unit)
    {
        switch (syntax)
        {
            case ConstScalarSyntax s:
                var value = s.Value switch
                {
                    decimal d => ExpressionValue.Number(d),
                    string str => ExpressionValue.Text(str),
                    bool b => ExpressionValue.Boolean(b),
                    _ => ExpressionValue.Null,
                };
                return ScriptConst.FromScalar(value, unit);
            case ConstMapSyntax m:
                var map = new Dictionary<string, ScriptConst>(StringComparer.Ordinal);
                foreach (var entry in m.Entries)
                {
                    if (map.ContainsKey(entry.Key))
                    {
                        Report(ScriptDiagnosticCodes.DuplicateMapKey, entry.KeySpan,
                            $"Duplicate map key '{entry.Key}'.", ("key", entry.Key));
                        continue;
                    }

                    map[entry.Key] = BuildConst(entry.Value, null);
                }

                return ScriptConst.FromMap(map, unit);
            default:
                return ScriptConst.FromScalar(ExpressionValue.Null, unit);
        }
    }

    // ───── rows, match ─────

    private void BindRows(RowsSyntax r)
    {
        if (options.IsLibrary)
        {
            Report(ScriptDiagnosticCodes.RowsInLibrary, new ScriptSpan(r.Span.Start, 4, r.Span.Line, r.Span.Column),
                "A library cannot contain 'rows'.");
        }

        foreach (var table in r.Tables)
        {
            var key = table.ToString();
            var seen = rows.SelectMany(c => c.Tables).Concat(r.Tables.TakeWhile(t => !ReferenceEquals(t, table)));
            if (seen.Any(t => string.Equals(t.ToString(), key, StringComparison.OrdinalIgnoreCase)))
            {
                Report(ScriptDiagnosticCodes.DuplicateTable, table.Span,
                    $"Table '{key}' is already used in another 'rows' clause.", ("table", key));
            }
        }

        if (r.Where is not null)
        {
            CollectMatch(r.Where);
        }

        if (r.Active is not null)
        {
            CollectMatch(r.Active);
        }

        rows.Add(new RowsClause(r.Tables, r.Where, r.Active));
    }

    private void CollectMatch(MatchNode node)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (node)
        {
            case MatchAnd a:
                CollectMatch(a.Left);
                CollectMatch(a.Right);
                break;
            case MatchOr o:
                CollectMatch(o.Left);
                CollectMatch(o.Right);
                break;
            case MatchNot n:
                CollectMatch(n.Operand);
                break;
            case MatchEquals e:
                AddMatchColumn(e.Column);
                CollectMatchValue(e.Value, e.Span);
                break;
            case MatchIn i:
                AddMatchColumn(i.Column);
                foreach (var v in i.Values)
                {
                    CollectMatchValue(v, i.Span);
                }

                break;
            default:
                break;
        }
    }

    private void AddMatchColumn(string column)
    {
        if (!matchColumns.Contains(column, StringComparer.Ordinal))
        {
            matchColumns.Add(column);
        }
    }

    private void CollectMatchValue(MatchValue value, ScriptSpan span)
    {
        if (value is MatchValue.Entry entry)
        {
            AddEntry(entry.RegistryCode, entry.EntryCode, span);
        }
    }

    private void AddEntry(string registry, string code, ScriptSpan span)
    {
        needsRegistries = true;
        AddRegistryCode(registry);
        if (!registryEntries.Any(e => e.RegistryCode == registry && e.EntryCode == code))
        {
            registryEntries.Add(new RegistryEntryRef(registry, code) { Span = span });
        }
    }

    // ───── функції ─────

    private void BindFunction(FunctionSyntax f)
    {
        var savedScope = scope;
        var savedAgg = aggregateStack.ToArray();
        aggregateStack.Clear();
        scope = new Scope(null);
        inFunction = true;
        currentFunction = f.Name;
        callGraph.TryAdd(f.Name, new HashSet<string>(StringComparer.Ordinal));

        var names = new List<string>();
        foreach (var p in f.Params)
        {
            if (names.Contains(p.Name, StringComparer.OrdinalIgnoreCase) || IsVisibleName(p.Name))
            {
                Report(ScriptDiagnosticCodes.DuplicateName, p.Span,
                    $"Parameter '{p.Name}' repeats an existing name.", ("name", p.Name));
            }

            names.Add(p.Name);
            scope.Names[p.Name] = Sym.Param;
        }

        var body = Lower(f.Body);
        functions[f.Name] = new ScriptFunction(f.Name, names, body);

        inFunction = false;
        currentFunction = null;
        scope = savedScope;
        foreach (var ctx in savedAgg.Reverse())
        {
            aggregateStack.Push(ctx);
        }
    }

    private void CheckRecursion()
    {
        foreach (var (name, syntax) in functionSyntax)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            stack.Push(name);
            var cyclic = false;
            while (stack.Count > 0 && !cyclic)
            {
                var current = stack.Pop();
                if (!callGraph.TryGetValue(current, out var targets))
                {
                    continue;
                }

                foreach (var target in targets)
                {
                    if (target == name)
                    {
                        cyclic = true;
                        break;
                    }

                    if (seen.Add(target))
                    {
                        stack.Push(target);
                    }
                }
            }

            if (cyclic)
            {
                Report(ScriptDiagnosticCodes.RecursiveFunction, syntax.NameSpan,
                    $"Function '{name}' calls itself (directly or through other functions); recursion is not allowed.",
                    ("name", name));
            }
        }
    }

    // ───── оператори ─────

    private List<ScriptStatement> BindStatements(IReadOnlyList<StatementSyntax> list, bool topLevel)
    {
        var result = new List<ScriptStatement>();
        foreach (var statement in list)
        {
            switch (statement)
            {
                case VarSyntax v:
                    result.Add(BindVar(v, topLevel));
                    break;
                case OutputSyntax o:
                    result.Add(BindOutput(o, topLevel));
                    break;
                case RejectSyntax r:
                    result.Add(new RejectStatement(Lower(r.Cond), Slice(r.Cond.Span)));
                    break;
                case SkipSyntax s:
                    if (!skipAllowed)
                    {
                        Report(ScriptDiagnosticCodes.SkipOutsideSubstanceLoop, s.Span,
                            "'skip when' is allowed only directly inside 'foreach substance'.");
                    }

                    result.Add(new SkipStatement(Lower(s.Cond)));
                    break;
                case IfSyntax i:
                    result.Add(BindIf(i));
                    break;
                case ForeachSubstanceSyntax f:
                    result.Add(BindForeachSubstance(f));
                    break;
                default:
                    break;
            }
        }

        return result;
    }

    private ScriptBlock BindBlock(BlockSyntax block, bool allowSkip)
    {
        var parent = scope;
        var savedSkip = skipAllowed;
        scope = new Scope(parent);
        skipAllowed = allowSkip;
        var statements = BindStatements(block.Statements, topLevel: false);
        scope = parent;
        skipAllowed = savedSkip;
        return new ScriptBlock(statements);
    }

    private VarStatement BindVar(VarSyntax v, bool topLevel)
    {
        var init = Lower(v.Init);
        if (!topLevel)
        {
            CheckBlockName(v.Name, v.NameSpan);
        }

        if (v.Unit is { } unit)
        {
            AddUnit(unit);
        }

        if (v.IsVisible && v.Unit is null)
        {
            Report(ScriptDiagnosticCodes.VisibleNeedsUnit, v.NameSpan,
                $"'visible var {v.Name}' needs a unit: 'visible var {v.Name} : unit = …'.", ("name", v.Name));
        }

        scope.Names[v.Name] = Sym.Var;
        if (topLevel)
        {
            members.Add(new MemberInfo(v.Name, MemberKind.Var, v.NameSpan.Line, v.Unit?.Code));
        }

        if (v.IsVisible && v.Unit is { } vu && !visibles.Any(x => x.Code == v.Name))
        {
            visibles.Add(new VisibleInfo(v.Name, vu.Code));
        }

        return new VarStatement(v.Name, v.Unit?.Code, v.IsVisible, init, Slice(v.Init.Span), v.NameSpan.Line);
    }

    private OutputStatement BindOutput(OutputSyntax o, bool topLevel)
    {
        if (options.IsLibrary)
        {
            Report(ScriptDiagnosticCodes.OutputInLibrary, o.CodeSpan, "A library cannot contain 'output'.");
        }

        if (o.PerVar is null)
        {
            if (foreachVar is not null)
            {
                Report(ScriptDiagnosticCodes.OutputNeedsPer, o.CodeSpan,
                    $"Inside 'foreach substance' the output '{o.Code}' needs 'per {foreachVar}'.",
                    ("code", o.Code), ("variable", foreachVar));
            }
        }
        else if (foreachVar is null)
        {
            Report(ScriptDiagnosticCodes.OutputOutsideSubstanceLoop, o.PerSpan,
                $"'per {o.PerVar}' is allowed only inside 'foreach substance'.", ("variable", o.PerVar));
        }
        else if (o.PerVar != foreachVar)
        {
            Report(ScriptDiagnosticCodes.UnknownName, o.PerSpan,
                $"'{o.PerVar}' is not the substance variable of the enclosing loop ('{foreachVar}').", ("name", o.PerVar));
        }

        var init = Lower(o.Init);
        AddUnit(o.Unit);
        if (!topLevel)
        {
            CheckBlockName(o.Code, o.CodeSpan);
        }

        RegisterOutput(o, topLevel);
        scope.Names[o.Code] = Sym.Output;
        return new OutputStatement(o.Code, o.Unit.Code, o.PerVar, init, Slice(o.Init.Span), o.CodeSpan.Line);
    }

    private void RegisterOutput(OutputSyntax o, bool topLevel)
    {
        var isPer = o.PerVar is not null;
        if (outputRegistry.TryGetValue(o.Code, out var existing))
        {
            if (topLevel && existing.TopLevel)
            {
                return; // дубль двох верхніх виходів уже показано як duplicateName
            }

            var sibling = existing.Path.Any(a => ifPath.Any(b => a.IfId == b.IfId && a.Branch != b.Branch));
            if (!sibling || existing.Info.UnitCode != o.Unit.Code || existing.Info.IsPerSubstance != isPer)
            {
                Report(ScriptDiagnosticCodes.DuplicateName, o.CodeSpan,
                    $"Output '{o.Code}' is already declared; the same code may repeat only in the 'if' and 'else' branches with the same unit.",
                    ("name", o.Code));
            }

            return;
        }

        var info = new OutputInfo(o.Code, o.Unit.Code, isPer, outputs.Count + 1);
        outputs.Add(info);
        outputRegistry[o.Code] = new OutputRecord(info, [.. ifPath], topLevel);
    }

    private IfStatement BindIf(IfSyntax i)
    {
        var cond = Lower(i.Cond);
        var id = ++ifCounter;
        ifPath.Add((id, 0));
        var then = BindBlock(i.Then, allowSkip: false);
        ifPath.RemoveAt(ifPath.Count - 1);

        ScriptBlock? @else = null;
        if (i.Else is not null)
        {
            ifPath.Add((id, 1));
            @else = BindBlock(i.Else, allowSkip: false);
            ifPath.RemoveAt(ifPath.Count - 1);
        }

        var codes = new List<string>();
        CollectOutputs(then.Statements, codes);
        if (@else is not null)
        {
            CollectOutputs(@else.Statements, codes);
        }

        return new IfStatement(cond, then, @else, codes);
    }

    private static void CollectOutputs(IEnumerable<ScriptStatement> statements, List<string> codes)
    {
        foreach (var s in statements)
        {
            switch (s)
            {
                case OutputStatement o when !codes.Contains(o.Code, StringComparer.Ordinal):
                    codes.Add(o.Code);
                    break;
                case IfStatement i:
                    foreach (var c in i.OutputCodes.Where(c => !codes.Contains(c, StringComparer.Ordinal)))
                    {
                        codes.Add(c);
                    }

                    break;
                case ForeachSubstanceStatement f:
                    CollectOutputs(f.Body.Statements, codes);
                    break;
                default:
                    break;
            }
        }
    }

    private ForeachSubstanceStatement BindForeachSubstance(ForeachSubstanceSyntax f)
    {
        if (foreachVar is not null)
        {
            Report(ScriptDiagnosticCodes.NestedSubstanceLoop, f.Span,
                "'foreach substance' cannot be nested.");
        }

        CheckBlockName(f.VarName, f.VarSpan);
        var codes = new List<string>();
        foreach (var code in f.Codes)
        {
            codes.Add(code.Code);
            if (!substances.Contains(code.Code, StringComparer.Ordinal))
            {
                substances.Add(code.Code);
            }
        }

        var parent = scope;
        scope = new Scope(parent);
        scope.Names[f.VarName] = Sym.Substance;
        var savedVar = foreachVar;
        foreachVar = f.VarName;
        var savedSkip = skipAllowed;
        var body = BindBlock(f.Body, allowSkip: true);
        foreachVar = savedVar;
        skipAllowed = savedSkip;
        scope = parent;
        return new ForeachSubstanceStatement(f.VarName, codes, body);
    }

    // ───── вирази ─────

    private AstNode Lower(Expr e)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (e)
        {
            case NumberExpr n:
                return At(new LiteralNode(n.Value, ExpressionValueType.Number), n.Span);
            case StringExpr s:
                return At(new LiteralNode(s.Value, ExpressionValueType.Text), s.Span);
            case BoolExpr b:
                return At(new LiteralNode(b.Value, ExpressionValueType.Boolean), b.Span);
            case NullExpr nl:
                return At(new LiteralNode(null, ExpressionValueType.Null), nl.Span);
            case NameExpr name:
                return LowerName(name);
            case RowExpr row:
                AddRowField(row.Field, row.IsOptional, false, row.Span);
                return At(new SymbolReferenceNode(SymbolKind.Argument, row.Field) { IsOptional = row.IsOptional }, row.Span);
            case KeyExpr key:
                AddRowField(key.Field, key.IsOptional, false, key.Span);
                return At(new FunctionNode("$key", [Lit(key.Field)]), key.Span);
            case PeriodExpr period:
                return LowerPeriod(period);
            case HeaderExpr header:
                return At(new SymbolReferenceNode(SymbolKind.Header, header.Name), header.Span);
            case EntryExpr entry:
                AddEntry(entry.Registry, entry.Code, entry.Span);
                return At(new FunctionNode("$entry", [Lit(entry.Registry), Lit(entry.Code)]), entry.Span);
            case CallExpr call:
                return LowerCall(call);
            case LibCallExpr lib:
                return LowerLibCall(lib);
            case AggregateExpr agg:
                return LowerAggregate(agg);
            case MemberExpr or IndexExpr:
                return LowerPostfix(e);
            case UnaryExpr u:
                return At(new UnaryNode(u.Op, Lower(u.Operand)), u.Span);
            case BinaryExpr bin:
                return At(new BinaryNode(bin.Op, Lower(bin.Left), Lower(bin.Right)), bin.Span);
            case TernaryExpr t:
                return At(new FunctionNode("if", [Lower(t.Cond), Lower(t.WhenTrue), Lower(t.WhenFalse)]), t.Span);
            case InExpr i:
                return At(new FunctionNode("in", [Lower(i.Value), .. i.Items.Select(Lower)]), i.Span);
            default:
                return Null();
        }
    }

    private void AddRowField(string name, bool optional, bool monthly, ScriptSpan span)
    {
        var field = rowFields.Find(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            field = new RowFieldBuilder(name, span);
            rowFields.Add(field);
        }

        field.AnyRequired |= !optional;
        field.Monthly |= monthly;
    }

    private AstNode LowerPeriod(PeriodExpr p)
    {
        switch (p.Property)
        {
            case "Days" or "Hours" or "Seconds" or "Start" or "End" or "Year":
                return At(new PeriodPropertyNode(p.Property, 0), p.Span);
            case "Month":
                return At(new PeriodPropertyNode("Sequence", 0), p.Span);
            case "EndKey":
                return At(new FunctionNode("$periodEndKey", []), p.Span);
            default:
                Report(ScriptDiagnosticCodes.UnknownName, p.Span,
                    $"Unknown period property '{p.Property}'. Available: Days, Hours, Seconds, Start, End, Year, Month, EndKey.",
                    ("name", "period." + p.Property));
                return Null();
        }
    }

    private AstNode LowerName(NameExpr n)
    {
        for (var s = scope; s is not null; s = s.Parent)
        {
            if (s.Names.TryGetValue(n.Name, out var kind))
            {
                if (kind == Sym.Substance)
                {
                    Report(ScriptDiagnosticCodes.UnknownName, n.Span,
                        $"'{n.Name}' is a substance variable: use '{n.Name}.code'.", ("name", n.Name));
                    return Null();
                }

                return At(new SymbolReferenceNode(SymbolKind.Formula, n.Name), n.Span);
            }
        }

        if (consts.TryGetValue(n.Name, out var c))
        {
            if (c.IsMap)
            {
                Report(ScriptDiagnosticCodes.MapNotScalar, n.Span,
                    $"'{n.Name}' is a map: index it, for example {n.Name}[key].", ("name", n.Name));
                return Null();
            }

            return At(new SymbolReferenceNode(SymbolKind.Formula, n.Name), n.Span);
        }

        if (tables.ContainsKey(n.Name))
        {
            Report(ScriptDiagnosticCodes.TableColumnOutsideAggregate, n.Span,
                $"'{n.Name}' is a table alias: use '{n.Name}.Column' inside an aggregate or Count({n.Name}).",
                ("alias", n.Name));
            return Null();
        }

        if (!inFunction && topLevelNames.Contains(n.Name))
        {
            Report(ScriptDiagnosticCodes.ForwardReference, n.Span,
                $"'{n.Name}' is used before its declaration.", ("name", n.Name));
            return Null();
        }

        Report(ScriptDiagnosticCodes.UnknownName, n.Span, $"Unknown name '{n.Name}'.", ("name", n.Name));
        return Null();
    }

    // ───── виклики ─────

    private AstNode LowerCall(CallExpr call)
    {
        var args = call.Args.Select(Lower).ToList();
        var name = call.Name;

        if (functionSyntax.TryGetValue(name, out var user))
        {
            if (user.Params.Count != args.Count)
            {
                Report(ScriptDiagnosticCodes.ArgumentCount, call.NameSpan,
                    $"Function '{name}' takes {user.Params.Count} argument(s), got {args.Count}.",
                    ("name", name), ("expected", user.Params.Count.ToString(CultureInfo.InvariantCulture)),
                    ("actual", args.Count.ToString(CultureInfo.InvariantCulture)));
            }

            if (currentFunction is not null)
            {
                callGraph[currentFunction].Add(name);
            }

            return At(new FunctionNode("$call:" + name, args), call.Span);
        }

        var signature = DialectCatalog.Find(name);
        if (signature is null)
        {
            var advice = DialectCatalog.Advice(name);
            var correction = DialectCatalog.CaseCorrection(name)
                             ?? functionSyntax.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            var pairs = new List<(string, string)> { ("name", name) };
            if (advice is not null)
            {
                pairs.Add(("advice", advice));
            }

            if (correction is not null)
            {
                pairs.Add(("suggestion", correction));
            }

            Report(ScriptDiagnosticCodes.UnknownFunction, call.NameSpan,
                $"Unknown function '{name}'." + (advice is null ? string.Empty : " " + advice + "."), [.. pairs]);
            return Null();
        }

        if (!DialectCatalog.IsAllowedIn(name, options.NumericMode))
        {
            Report(ScriptDiagnosticCodes.FunctionNotAllowedInMode, call.NameSpan,
                $"Function '{name}' is not available in {options.NumericMode} numeric mode.",
                ("name", name), ("mode", options.NumericMode.ToString()));
        }

        if (args.Count < signature.MinArgs || (signature.MaxArgs is { } max && args.Count > max))
        {
            var expected = signature.MaxArgs == signature.MinArgs
                ? signature.MinArgs.ToString(CultureInfo.InvariantCulture)
                : signature.MinArgs.ToString(CultureInfo.InvariantCulture) + (signature.MaxArgs is { } m2 ? "–" + m2.ToString(CultureInfo.InvariantCulture) : "+");
            Report(ScriptDiagnosticCodes.ArgumentCount, call.NameSpan,
                $"Function '{name}' takes {expected} argument(s), got {args.Count}.",
                ("name", name), ("expected", expected), ("actual", args.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (RegistryFunctions.Contains(name) || name == "SUBSTANCE_APPLICABLE")
        {
            needsRegistries = true;
            if (call.Args.Count > 0 && call.Args[0] is StringExpr reg)
            {
                AddRegistryCode(reg.Value);
            }
        }
        else if (name == "CURRENT_SUBSTANCE")
        {
            needsRegistries = true;
        }

        return At(new FunctionNode(name, args), call.Span);
    }

    private AstNode LowerLibCall(LibCallExpr call)
    {
        var args = call.Args.Select(Lower).ToList();
        if (!imports.TryGetValue(call.Library, out var lib))
        {
            if (!globalKinds.ContainsKey(call.Library))
            {
                Report(ScriptDiagnosticCodes.UnknownImport, new ScriptSpan(call.Span.Start, call.Library.Length, call.Span.Line, call.Span.Column),
                    $"Library '{call.Library}' is not imported.", ("name", call.Library));
            }

            return Null();
        }

        if (!lib.Functions.TryGetValue(call.Name, out var function))
        {
            Report(ScriptDiagnosticCodes.UnknownImportMember, call.NameSpan,
                $"Library '{call.Library}' has no function '{call.Name}'.", ("library", call.Library), ("member", call.Name));
            return Null();
        }

        if (function.Parameters.Count != args.Count)
        {
            Report(ScriptDiagnosticCodes.ArgumentCount, call.NameSpan,
                $"Function '{call.Library}.{call.Name}' takes {function.Parameters.Count} argument(s), got {args.Count}.",
                ("name", call.Library + "." + call.Name),
                ("expected", function.Parameters.Count.ToString(CultureInfo.InvariantCulture)),
                ("actual", args.Count.ToString(CultureInfo.InvariantCulture)));
        }

        return At(new FunctionNode("$call:" + call.Library + "." + call.Name, args), call.Span);
    }

    // ───── агрегати ─────

    private AstNode LowerAggregate(AggregateExpr a)
    {
        if (inFunction)
        {
            Report(ScriptDiagnosticCodes.AggregateNotAllowedHere, a.NameSpan,
                "An aggregate cannot be used in a function body.", ("name", a.Kind.ToString()));
            return Null();
        }

        var context = new AggregateContext();
        AstNode? value = null;
        AstNode? where = null;
        string alias;

        if (a.Kind is AggregateKind.Count or AggregateKind.Any)
        {
            if (a.Arg is not NameExpr aliasName || !tables.ContainsKey(aliasName.Name))
            {
                Report(ScriptDiagnosticCodes.UnknownTableAlias, a.Arg.Span,
                    $"{a.Kind} expects a table alias declared with 'table Alias = Table;'.",
                    ("name", a.Arg is NameExpr n ? n.Name : Slice(a.Arg.Span)));
                return Null();
            }

            alias = aliasName.Name;
            context.Alias = alias;
            aggregateStack.Push(context);
            where = a.Where is null ? null : Lower(a.Where);
            aggregateStack.Pop();
        }
        else
        {
            aggregateStack.Push(context);
            value = Lower(a.Arg);
            if (context.Referenced.Count == 0)
            {
                Report(ScriptDiagnosticCodes.AggregateNeedsTable, a.Arg.Span,
                    $"The value of {a.Kind}(…) must read a column of a declared table: Alias.Column.", ("name", a.Kind.ToString()));
                alias = string.Empty;
            }
            else
            {
                alias = context.Referenced.First();
                if (context.Referenced.Count > 1)
                {
                    Report(ScriptDiagnosticCodes.AggregateManyTables, a.Arg.Span,
                        $"The value of {a.Kind}(…) must read exactly one table, found: {string.Join(", ", context.Referenced)}.",
                        ("name", a.Kind.ToString()), ("tables", string.Join(", ", context.Referenced)));
                }
            }

            context.Alias = alias;
            if (context.Nested.Contains(alias))
            {
                Report(ScriptDiagnosticCodes.NestedAggregateSameTable, a.Span,
                    $"A nested aggregate over the same table '{alias}' is not allowed.", ("alias", alias));
            }

            where = a.Where is null ? null : Lower(a.Where);
            aggregateStack.Pop();
        }

        foreach (var outer in aggregateStack)
        {
            if (outer.Alias == alias && alias.Length > 0)
            {
                Report(ScriptDiagnosticCodes.NestedAggregateSameTable, a.Span,
                    $"A nested aggregate over the same table '{alias}' is not allowed.", ("alias", alias));
            }

            outer.Nested.Add(alias);
        }

        var index = aggregates.Count;
        aggregates.Add(new AggregateInfo(a.Kind, alias, value, where));
        return At(new FunctionNode("$agg", [Lit(index)]), a.Span);
    }

    private AstNode LowerCell(string alias, string column, Expr? monthKey, ScriptSpan span)
    {
        var allowed = false;
        if (!inFunction && aggregateStack.Count > 0)
        {
            var inner = aggregateStack.Peek();
            if (inner.Alias is null)
            {
                allowed = true;
                if (!aggregateStack.Skip(1).Any(c => c.Alias == alias))
                {
                    inner.Referenced.Add(alias);
                }
            }
            else
            {
                allowed = aggregateStack.Any(c => c.Alias == alias);
            }
        }

        if (!allowed)
        {
            Report(ScriptDiagnosticCodes.TableColumnOutsideAggregate, span,
                $"'{alias}.{column}' can be read only inside an aggregate over '{alias}'.", ("alias", alias), ("column", column));
            return Null();
        }

        if (!tableColumns.TryGetValue(alias, out var columns))
        {
            columns = [];
            tableColumns[alias] = columns;
        }

        if (!columns.Contains(column, StringComparer.Ordinal))
        {
            columns.Add(column);
        }

        return monthKey is null
            ? At(new FunctionNode("$cell", [Lit(alias), Lit(column)]), span)
            : At(new FunctionNode("$cellMonth", [Lit(alias), Lit(column), Lower(monthKey)]), span);
    }

    // ───── доступ через крапку й індекс ─────

    private AstNode LowerPostfix(Expr e)
    {
        var accessors = new List<Accessor>();
        var current = e;
        while (true)
        {
            if (current is MemberExpr m)
            {
                accessors.Add(new Accessor(m.Name, null, m.NameSpan));
                current = m.Target;
            }
            else if (current is IndexExpr ix)
            {
                accessors.Add(new Accessor(null, ix.Keys, ix.Span));
                current = ix.Target;
            }
            else
            {
                break;
            }
        }

        accessors.Reverse();
        var span = e.Span;

        if (current is RowExpr row && accessors[0].Keys is { } monthKeys)
        {
            if (monthKeys.Count != 1)
            {
                Report(ScriptDiagnosticCodes.ArgumentCount, accessors[0].Span,
                    "A monthly column takes exactly one index: row.Column[month].",
                    ("name", "row." + row.Field), ("expected", "1"), ("actual", monthKeys.Count.ToString(CultureInfo.InvariantCulture)));
            }

            AddRowField(row.Field, row.IsOptional, true, row.Span);
            var month = Lower(monthKeys[0]);
            foreach (var k in monthKeys.Skip(1))
            {
                Lower(k);
            }

            ReportExtraAccess(accessors, 1);
            return At(new FunctionNode("$month", [Lit(row.Field), month, new LiteralNode(row.IsOptional, ExpressionValueType.Boolean)]), span);
        }

        if (current is NameExpr n)
        {
            if (TryLowerNamedPostfix(n, accessors, span, out var node))
            {
                return node;
            }
        }

        var baseNode = Lower(current);
        Report(ScriptDiagnosticCodes.IndexOnNonMap, accessors[0].Span,
            "Indexing or '.' access is possible only on a map constant, a library, a table alias or a substance variable.");
        foreach (var accessor in accessors.Where(a => a.Keys is not null))
        {
            foreach (var key in accessor.Keys!)
            {
                Lower(key);
            }
        }

        return baseNode;
    }

    private void ReportExtraAccess(List<Accessor> accessors, int from)
    {
        if (accessors.Count > from)
        {
            Report(ScriptDiagnosticCodes.IndexOnNonMap, accessors[from].Span,
                "The value is not a map: further indexing is not possible.");
        }
    }

    private bool TryLowerNamedPostfix(NameExpr n, List<Accessor> accessors, ScriptSpan span, out AstNode node)
    {
        node = Null();
        var name = n.Name;

        // Локальні імена (змінні, параметри, змінна речовини) мають перевагу над глобальними.
        Sym? local = null;
        for (var s = scope; s is not null && local is null; s = s.Parent)
        {
            if (s.Names.TryGetValue(name, out var kind))
            {
                local = kind;
            }
        }

        if (local == Sym.Substance)
        {
            if (accessors.Count == 1 && accessors[0].Member == "code")
            {
                node = At(new SymbolReferenceNode(SymbolKind.Formula, name + ".code"), span);
            }
            else
            {
                Report(ScriptDiagnosticCodes.UnknownName, span,
                    $"A substance variable has one property: '{name}.code'.", ("name", span.Length > 0 ? Slice(span) : name));
            }

            return true;
        }

        if (local is not null)
        {
            node = At(new SymbolReferenceNode(SymbolKind.Formula, name), n.Span);
            ReportExtraAccess(accessors, 0);
            return true;
        }

        if (consts.TryGetValue(name, out var root))
        {
            if (!root.IsMap)
            {
                node = At(new SymbolReferenceNode(SymbolKind.Formula, name), n.Span);
                ReportExtraAccess(accessors, 0);
                return true;
            }

            node = LowerMapLookup(name, root, accessors, 0, span);
            return true;
        }

        if (tables.ContainsKey(name))
        {
            if (accessors[0].Member is { } column)
            {
                if (accessors.Count >= 2 && accessors[1].Keys is { Count: 1 } keys)
                {
                    node = LowerCell(name, column, keys[0], span);
                    ReportExtraAccess(accessors, 2);
                }
                else
                {
                    node = LowerCell(name, column, null, span);
                    ReportExtraAccess(accessors, 1);
                }
            }
            else
            {
                Report(ScriptDiagnosticCodes.TableColumnOutsideAggregate, span,
                    $"'{name}' is a table alias: use '{name}.Column'.", ("alias", name));
            }

            return true;
        }

        if (imports.TryGetValue(name, out var lib))
        {
            node = LowerImportMember(name, lib, accessors, span);
            return true;
        }

        return false;
    }

    private AstNode LowerImportMember(string libraryName, CompiledScript lib, List<Accessor> accessors, ScriptSpan span)
    {
        if (accessors[0].Member is not { } member)
        {
            Report(ScriptDiagnosticCodes.UnknownName, span,
                $"'{libraryName}' is a library: use '{libraryName}.Name'.", ("name", libraryName));
            return Null();
        }

        var info = lib.Members.FirstOrDefault(m => m.Name == member && m.Kind != MemberKind.Function);
        if (info is null)
        {
            Report(ScriptDiagnosticCodes.UnknownImportMember, accessors[0].Span,
                $"Library '{libraryName}' has no value '{member}'.", ("library", libraryName), ("member", member));
            return Null();
        }

        var full = libraryName + "." + member;
        if (info.Kind == MemberKind.Const && info.IsMap && lib.Constants.TryGetValue(member, out var root))
        {
            return LowerMapLookup(full, root, accessors, 1, span);
        }

        ReportExtraAccess(accessors, 1);
        return At(new SymbolReferenceNode(SymbolKind.Formula, full), span);
    }

    private AstNode LowerMapLookup(string mapName, ScriptConst root, List<Accessor> accessors, int from, ScriptSpan span)
    {
        var keys = new List<AstNode>();
        var literals = new List<string?>();
        for (var i = from; i < accessors.Count; i++)
        {
            var a = accessors[i];
            if (a.Member is { } member)
            {
                keys.Add(Lit(member));
                literals.Add(member);
            }
            else
            {
                foreach (var key in a.Keys!)
                {
                    keys.Add(Lower(key));
                    literals.Add(key switch
                    {
                        StringExpr s => s.Value,
                        NumberExpr num => ScriptNumbers.KeyText(num.Value),
                        _ => null,
                    });
                }
            }
        }

        if (keys.Count == 0)
        {
            Report(ScriptDiagnosticCodes.MapNotScalar, span,
                $"'{mapName}' is a map: index it, for example {mapName}[key].", ("name", mapName));
            return Null();
        }

        if (literals.All(l => l is not null))
        {
            var cur = root;
            for (var i = 0; i < literals.Count; i++)
            {
                if (!cur.IsMap)
                {
                    Report(ScriptDiagnosticCodes.IndexOnNonMap, span,
                        $"'{mapName}' has fewer nesting levels than the indices used.", ("name", mapName));
                    return Null();
                }

                if (!cur.Map!.TryGetValue(literals[i]!, out var next))
                {
                    Report(ScriptDiagnosticCodes.UnknownMapKey, span,
                        $"The map '{mapName}' has no key '{literals[i]}'.", ("map", mapName), ("key", literals[i]!));
                    return Null();
                }

                cur = next;
            }

            if (cur.IsMap)
            {
                Report(ScriptDiagnosticCodes.MapNotScalar, span,
                    $"'{mapName}' indexed this way is still a map: add the next key.", ("name", mapName));
                return Null();
            }
        }
        else
        {
            var (min, max) = LeafDepth(root);
            if (keys.Count < min)
            {
                Report(ScriptDiagnosticCodes.MapNotScalar, span,
                    $"'{mapName}' has {min} nesting level(s); {keys.Count} key(s) are not enough to reach a value.", ("name", mapName));
            }
            else if (keys.Count > max)
            {
                Report(ScriptDiagnosticCodes.IndexOnNonMap, span,
                    $"'{mapName}' has {max} nesting level(s); {keys.Count} keys are too many.", ("name", mapName));
            }
        }

        return At(new FunctionNode("$lookup", [Lit(mapName), .. keys]), span);
    }

    private static (int Min, int Max) LeafDepth(ScriptConst c)
    {
        if (!c.IsMap)
        {
            return (0, 0);
        }

        if (c.Map!.Count == 0)
        {
            return (1, 1);
        }

        var children = c.Map.Values.Select(LeafDepth).ToList();
        return (1 + children.Min(x => x.Min), 1 + children.Max(x => x.Max));
    }
}
