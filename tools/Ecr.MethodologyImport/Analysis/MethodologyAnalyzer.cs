using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Model;
using Ecr.MethodologyImport.Reading;

namespace Ecr.MethodologyImport.Analysis;

/// <summary>
/// Сухий прогін: рахує методології/формули/константи, розбирає посилання з <c>FInfo_Arguments</c>
/// (<c>;</c>-список з токенами <c>@параметр</c>, <c>!Формула</c>, <c>CST.Константа</c>), резолвить їх і шукає цикли.
/// Резолвінг: спершу у власній версії методології, потім у бібліотеці (за замовчуванням «Common», усі її версії).
/// Нерезолвне посилання й цикл — БЛОКЕРИ (код виходу ≠ 0); ціль ніколи не вгадується.
/// </summary>
public static partial class MethodologyAnalyzer
{
    public const string DefaultLibrary = "Common";
    private const string ConstantPrefix = "CST.";

    [GeneratedRegex(@"^[A-Za-z_]\w*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"(?<![\w.])CST\.([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex ConstantInTextRegex();

    [GeneratedRegex(@"(?<![\w!])!([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaInTextRegex();

    public static AnalysisReport Analyze(
        MethodologyModel model, AfReadStats readStats, string library = DefaultLibrary)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(readStats);
        var formulas = model.Formulas;

        // ---- Індекси цілей ---------------------------------------------------------------------------
        var formulaScope = new Dictionary<(string, string), Dictionary<string, List<int>>>();
        var libraryFormulas = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var formulasByName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < formulas.Count; i++)
        {
            var f = formulas[i];
            Index(formulaScope, (f.Methodology, f.MethodologyVersion), f.Name, i);
            Add(formulasByName, f.Name, i);
            if (string.Equals(f.Methodology, library, StringComparison.Ordinal))
            {
                Add(libraryFormulas, f.Name, i);
            }
        }

        var constantScope = new Dictionary<(string, string), HashSet<string>>();
        var libraryConstants = new HashSet<string>(StringComparer.Ordinal);
        var constantsByName = new Dictionary<string, string>(StringComparer.Ordinal); // ім'я → методологія (для підказки)
        var constantsByParameter = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in model.Constants)
        {
            var key = (c.Methodology, c.MethodologyVersion);
            if (!constantScope.TryGetValue(key, out var set))
            {
                constantScope[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.Add(c.Name);
            constantsByName.TryAdd(c.Name, c.Methodology);
            if (c.Parameter.Length > 0)
            {
                constantsByParameter.TryAdd(c.Parameter, c.Methodology);
            }

            if (string.Equals(c.Methodology, library, StringComparison.Ordinal))
            {
                libraryConstants.Add(c.Name);
            }
        }

        // ---- Посилання ------------------------------------------------------------------------------
        var unresolved = new List<UnresolvedReference>();
        var edges = new List<int>[formulas.Count];
        var perVersion = new Dictionary<(string, string), VersionCounters>();
        int fTotal = 0, fResolved = 0, fCross = 0, cTotal = 0, cResolved = 0, cCross = 0;
        int paramArgs = 0, otherArgs = 0, trimmedTokens = 0;
        var undeclared = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < formulas.Count; i++)
        {
            var f = formulas[i];
            var counters = Counters(perVersion, (f.Methodology, f.MethodologyVersion));
            var declared = new HashSet<string>(StringComparer.Ordinal);

            foreach (var raw in f.Arguments.Split(';'))
            {
                var token = raw.Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                if (token.Length != raw.Length)
                {
                    trimmedTokens++;
                }

                if (token[0] == '@')
                {
                    paramArgs++;
                }
                else if (token[0] == '!')
                {
                    var name = token[1..].Trim();
                    declared.Add("!" + name);
                    fTotal++;
                    counters.FormulaRefs++;
                    var targets = FindFormula(f, name, formulaScope, libraryFormulas);
                    if (targets is not null)
                    {
                        fResolved++;
                        if (!string.Equals(formulas[targets[0]].Methodology, f.Methodology, StringComparison.Ordinal))
                        {
                            fCross++;
                        }

                        (edges[i] ??= []).AddRange(targets);
                    }
                    else
                    {
                        counters.Unresolved++;
                        unresolved.Add(new UnresolvedReference(
                            ReferenceKind.Formula, f.Methodology, f.MethodologyVersion, f.Name, f.Version,
                            name, raw, FormulaHint(f, name, formulaScope, formulasByName, formulas), f.Path));
                    }
                }
                else if (token.StartsWith(ConstantPrefix, StringComparison.Ordinal))
                {
                    var name = token[ConstantPrefix.Length..].Trim();
                    declared.Add(ConstantPrefix + name);
                    cTotal++;
                    counters.ConstantRefs++;
                    var inOwn = constantScope.TryGetValue((f.Methodology, f.MethodologyVersion), out var own)
                                && own.Contains(name);
                    if (inOwn || libraryConstants.Contains(name))
                    {
                        cResolved++;
                        if (!inOwn)
                        {
                            cCross++;
                        }
                    }
                    else
                    {
                        counters.Unresolved++;
                        unresolved.Add(new UnresolvedReference(
                            ReferenceKind.Constant, f.Methodology, f.MethodologyVersion, f.Name, f.Version,
                            name, raw, ConstantHint(name, constantsByName, constantsByParameter), f.Path));
                    }
                }
                else
                {
                    otherArgs++;
                }
            }

            // Токени в тексті формули, яких немає в списку аргументів: CLR такий токен у вираз не підставляє.
            foreach (var t in TokensInText(f.Text))
            {
                if (!declared.Contains(t))
                {
                    undeclared[t] = undeclared.GetValueOrDefault(t) + 1;
                }
            }
        }

        // ---- Цикли ---------------------------------------------------------------------------------
        var cycles = FindCycles(formulas, edges);

        // ---- Зведення ------------------------------------------------------------------------------
        var orderedUnresolved = unresolved
            .OrderBy(u => u.Methodology, StringComparer.Ordinal)
            .ThenBy(u => u.MethodologyVersion, StringComparer.Ordinal)
            .ThenBy(u => u.Formula, StringComparer.Ordinal)
            .ThenBy(u => u.FormulaVersion, StringComparer.Ordinal)
            .ThenBy(u => u.Kind)
            .ThenBy(u => u.Token, StringComparer.Ordinal)
            .ToList();

        foreach (var c in model.Constants)
        {
            Counters(perVersion, (c.Methodology, c.MethodologyVersion)).Constants++;
        }

        foreach (var f in formulas)
        {
            Counters(perVersion, (f.Methodology, f.MethodologyVersion)).Formulas++;
        }

        var byMethodology = perVersion
            .GroupBy(kv => kv.Key.Item1, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new MethodologySummary(
                g.Key,
                g.OrderBy(kv => kv.Key.Item2, StringComparer.Ordinal)
                    .Select(kv => new VersionSummary(
                        kv.Key.Item2, kv.Value.Formulas, kv.Value.Constants,
                        kv.Value.FormulaRefs, kv.Value.ConstantRefs, kv.Value.Unresolved))
                    .ToList()))
            .ToList();

        var blockers = new List<string>();
        if (formulas.Count == 0 && model.Constants.Count == 0)
        {
            blockers.Add("NO_METHODOLOGY_DATA: не розпізнано жодної формули чи константи — читач AF XML, найімовірніше, не збігається з форматом файла");
        }

        if (orderedUnresolved.Count > 0)
        {
            blockers.Add($"UNRESOLVED_REFERENCES: {orderedUnresolved.Count}");
        }

        if (cycles.Count > 0)
        {
            blockers.Add($"REFERENCE_CYCLES: {cycles.Count}");
        }

        var reader = new ReaderSummary(
            readStats.Elements, readStats.Attributes, readStats.ElementsWithoutName, readStats.DuplicateAttributes,
            new SortedDictionary<string, long>(readStats.Unrecognized, StringComparer.Ordinal),
            model.BuildStats.FormulaElements, model.BuildStats.ConstantElements,
            model.BuildStats.OtherElementsWithAttributes, model.BuildStats.MethodologyFromPath,
            model.BuildStats.ResolvedFromConfigString, model.BuildStats.UnresolvedConfigStrings);

        return new AnalysisReport(
            Methodologies: byMethodology.Count,
            MethodologyVersions: byMethodology.Sum(m => m.Versions.Count),
            Formulas: formulas.Count,
            Constants: model.Constants.Count,
            DistinctConstantNames: constantsByName.Count,
            FormulaReferences: new ReferenceTotals(fTotal, fResolved, fTotal - fResolved, fCross),
            ConstantReferences: new ReferenceTotals(cTotal, cResolved, cTotal - cResolved, cCross),
            ParameterArguments: paramArgs,
            OtherArguments: otherArgs,
            TrimmedFields: model.BuildStats.TrimmedFields,
            TrimmedArgumentTokens: trimmedTokens,
            DuplicateFormulaKeys: (int)model.BuildStats.DuplicateFormulaKeys,
            ByMethodology: byMethodology,
            Unresolved: orderedUnresolved,
            UnresolvedTokens: Top(orderedUnresolved.GroupBy(u => Prefix(u.Kind) + u.Token, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
            UndeclaredInTextTotal: undeclared.Values.Sum(),
            UndeclaredInText: Top(undeclared),
            Cycles: cycles,
            Reader: reader,
            Blockers: blockers);
    }

    private static string Prefix(ReferenceKind kind) => kind == ReferenceKind.Formula ? "!" : ConstantPrefix;

    private static IEnumerable<string> TokensInText(string text)
    {
        foreach (Match m in FormulaInTextRegex().Matches(text))
        {
            yield return "!" + m.Groups[1].Value;
        }

        foreach (Match m in ConstantInTextRegex().Matches(text))
        {
            yield return ConstantPrefix + m.Groups[1].Value;
        }
    }

    private static List<TokenCount> Top(Dictionary<string, int> counts)
        => counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new TokenCount(kv.Key, kv.Value))
            .ToList();

    private static List<int>? FindFormula(
        FormulaDef source,
        string name,
        Dictionary<(string, string), Dictionary<string, List<int>>> scope,
        Dictionary<string, List<int>> library)
    {
        if (!IdentifierRegex().IsMatch(name))
        {
            return null;
        }

        if (scope.TryGetValue((source.Methodology, source.MethodologyVersion), out var own)
            && own.TryGetValue(name, out var local))
        {
            return local;
        }

        return library.TryGetValue(name, out var lib) ? lib : null;
    }

    private static string FormulaHint(
        FormulaDef source,
        string name,
        Dictionary<(string, string), Dictionary<string, List<int>>> scope,
        Dictionary<string, List<int>> byName,
        IReadOnlyList<FormulaDef> formulas)
    {
        if (!IdentifierRegex().IsMatch(name))
        {
            return "некоректне ім'я формули (порожнє або з недопустимими символами)";
        }

        if (byName.TryGetValue(name, out var other))
        {
            var where = other.Select(i => formulas[i]).Select(f => $"{f.Methodology}/{f.MethodologyVersion}")
                .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            return $"є в іншій методології/версії без імпорту: {string.Join(", ", where)}";
        }

        foreach (var (_, names) in scope)
        {
            var match = names.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return $"відрізняється лише регістром від «{match}»";
            }
        }

        return $"формули з таким іменем немає ні в {source.Methodology}/{source.MethodologyVersion}, ні в бібліотеці";
    }

    private static string ConstantHint(
        string name, Dictionary<string, string> byName, Dictionary<string, string> byParameter)
    {
        if (!IdentifierRegex().IsMatch(name))
        {
            return "некоректне ім'я константи (порожнє або з недопустимими символами)";
        }

        if (byName.TryGetValue(name, out var m))
        {
            return $"є в методології «{m}» без імпорту";
        }

        return byParameter.TryGetValue(name, out var p)
            ? $"збігається лише з CInfo_Parameter у методології «{p}» (не з іменем константи)"
            : "константи з таким іменем немає ні у власній версії, ні в бібліотеці";
    }

    // ---- Цикли: ітеративний Tarjan (SCC), без рекурсії — графи можуть бути глибокими ----------------
    private static List<ReferenceCycle> FindCycles(IReadOnlyList<FormulaDef> formulas, List<int>[] edges)
    {
        var n = formulas.Count;
        var index = new int[n];
        var low = new int[n];
        var onStack = new bool[n];
        Array.Fill(index, -1);
        var stack = new Stack<int>();
        var counter = 0;
        var result = new List<ReferenceCycle>();

        for (var start = 0; start < n; start++)
        {
            if (index[start] != -1)
            {
                continue;
            }

            var work = new Stack<(int Node, int Next)>();
            work.Push((start, 0));
            index[start] = low[start] = counter++;
            stack.Push(start);
            onStack[start] = true;

            while (work.Count > 0)
            {
                var (v, next) = work.Pop();
                var outgoing = edges[v];
                if (outgoing is not null && next < outgoing.Count)
                {
                    work.Push((v, next + 1));
                    var w = outgoing[next];
                    if (index[w] == -1)
                    {
                        index[w] = low[w] = counter++;
                        stack.Push(w);
                        onStack[w] = true;
                        work.Push((w, 0));
                    }
                    else if (onStack[w])
                    {
                        low[v] = Math.Min(low[v], index[w]);
                    }

                    continue;
                }

                if (low[v] == index[v])
                {
                    var component = new List<int>();
                    int w2;
                    do
                    {
                        w2 = stack.Pop();
                        onStack[w2] = false;
                        component.Add(w2);
                    }
                    while (w2 != v);

                    var selfLoop = component.Count == 1 && edges[v] is not null && edges[v].Contains(v);
                    if (component.Count > 1 || selfLoop)
                    {
                        result.Add(new ReferenceCycle(component
                            .Select(i => formulas[i].Key)
                            .OrderBy(k => k, StringComparer.Ordinal)
                            .ToList()));
                    }
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[v]);
                }
            }
        }

        return result
            .OrderBy(c => c.Formulas[0], StringComparer.Ordinal)
            .ThenBy(c => c.Formulas.Count)
            .ToList();
    }

    private static void Index(
        Dictionary<(string, string), Dictionary<string, List<int>>> scope, (string, string) key, string name, int i)
    {
        if (!scope.TryGetValue(key, out var names))
        {
            scope[key] = names = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        }

        Add(names, name, i);
    }

    private static void Add(Dictionary<string, List<int>> map, string name, int i)
    {
        if (!map.TryGetValue(name, out var list))
        {
            map[name] = list = [];
        }

        list.Add(i);
    }

    private static VersionCounters Counters(Dictionary<(string, string), VersionCounters> map, (string, string) key)
    {
        if (!map.TryGetValue(key, out var c))
        {
            map[key] = c = new VersionCounters();
        }

        return c;
    }

    private sealed class VersionCounters
    {
        public int Formulas;
        public int Constants;
        public int FormulaRefs;
        public int ConstantRefs;
        public int Unresolved;
    }
}
