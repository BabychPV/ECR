// src/Ecr.Application/DocumentDependencies/DependencyGraphAssembler.cs
using Ecr.Expressions.Binding;

namespace Ecr.Application.DocumentDependencies;

/// <summary>
/// B179 L1: збирає граф із сирих ребер — без бази, тому перевіряється юніт-тестами.
/// </summary>
/// <remarks>
/// Згортання до рівня шаблонів (дизайн §2): <c>T_src → T_c</c>, якщо <c>T_src</c> наповнює довідник R (або R', що
/// правилами довідника читає R) і формула <c>T_c</c> читає R, або методологія, прив'язана до <c>T_c</c>, читає R.
/// ⚠ Читання через правило довідника — <see cref="PeriodScope.All"/>: період похідних рядків невідомий.
/// </remarks>
public static class DependencyGraphAssembler
{
    /// <summary>Будує знімок.</summary>
    /// <param name="inputs">Сирі ребра.</param>
    public static DependencyGraphSnapshot Assemble(DependencyGraphInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var edges = new Dictionary<(DependencyNodeRef From, DependencyNodeRef To, DependencyEdgeKind Kind), (PeriodScope Scope, List<DependencyEvidence> Evidence)>();

        void Add(DependencyNodeRef from, DependencyNodeRef to, DependencyEdgeKind kind, PeriodScope scope, DependencyEvidence evidence)
        {
            var key = (from, to, kind);
            if (edges.TryGetValue(key, out var known))
            {
                known.Evidence.Add(evidence);
                edges[key] = (known.Scope.Join(scope), known.Evidence);
            }
            else
            {
                edges[key] = (scope, [evidence]);
            }
        }

        static DependencyNodeRef T(int id) => new(DependencyNodeKind.Template, id);
        static DependencyNodeRef R(int id) => new(DependencyNodeKind.Registry, id);
        static DependencyNodeRef M(int id) => new(DependencyNodeKind.Methodology, id);

        foreach (var binding in inputs.Bindings)
        {
            Add(T(binding.SourceTemplateId), R(binding.RegistryDefId), DependencyEdgeKind.Fills, PeriodScope.All,
                new DependencyEvidence("sourceBinding", binding.BindingId, null, binding.SourceState.ToString()));
        }

        foreach (var read in inputs.TemplateReads)
        {
            Add(R(read.RegistryDefId), T(read.TemplateId), DependencyEdgeKind.TemplateFormula, read.Scope,
                new DependencyEvidence(read.Source, read.FormulaDefId, read.TemplateVersionId, read.Detail));
        }

        foreach (var read in inputs.MethodologyReads)
        {
            Add(R(read.RegistryDefId), M(read.MethodologyId), DependencyEdgeKind.Methodology, read.Scope,
                new DependencyEvidence("registryUse", read.MethodologyVersionId, null, read.FormulaCode));
        }

        foreach (var write in inputs.MethodologyWrites)
        {
            Add(M(write.MethodologyId), T(write.TemplateId), DependencyEdgeKind.WritesInto, PeriodScope.All,
                new DependencyEvidence("calculationBinding", write.CalculationBindingId, null, write.Detail));
        }

        foreach (var read in inputs.MethodologyResultReads)
        {
            Add(M(read.SourceMethodologyId), M(read.ReaderMethodologyId), DependencyEdgeKind.MethodologyResult, PeriodScope.All,
                new DependencyEvidence("methodologyDependency", read.ReaderMethodologyId, null, null));
        }

        foreach (var rule in inputs.RuleReads)
        {
            Add(R(rule.ReadRegistryDefId), R(rule.RuleRegistryDefId), DependencyEdgeKind.RegistryRule, PeriodScope.All,
                new DependencyEvidence("registryRule", rule.RuleId, null, null));
        }

        var dependencies = Collapse(inputs, edges.ToDictionary(e => e.Key, e => e.Value.Scope));

        var templateIds = edges.Keys
            .SelectMany(k => new[] { k.From, k.To })
            .Where(r => r.Kind == DependencyNodeKind.Template)
            .Select(r => r.Id);
        var (levels, cycles) = TemplateGraphOrder.Compute(
            templateIds, dependencies.Select(d => (d.SourceTemplateId, d.ConsumerTemplateId)));

        var diagnostics = inputs.Diagnostics.ToList();
        diagnostics.AddRange(cycles.Select(c => new DependencyGraphDiagnostic(
            "cycle", string.Join(" → ", c.Select(id => inputs.TemplateCodes.GetValueOrDefault(id, $"#{id}"))))));

        var nodes = edges.Keys
            .SelectMany(k => new[] { k.From, k.To })
            .Distinct()
            .OrderBy(r => r.Kind).ThenBy(r => r.Id)
            .Select(r => new DependencyNode(r, CodeOf(inputs, r)))
            .ToList();

        return new DependencyGraphSnapshot(
            inputs.Revision,
            nodes,
            [.. edges.OrderBy(e => e.Key.Kind).ThenBy(e => e.Key.From.Id).ThenBy(e => e.Key.To.Id)
                .Select(e => new DependencyEdge(e.Key.From, e.Key.To, e.Key.Kind, e.Value.Scope, e.Value.Evidence))],
            dependencies,
            levels,
            cycles,
            diagnostics);
    }

    private static List<TemplateDependency> Collapse(
        DependencyGraphInputs inputs,
        Dictionary<(DependencyNodeRef From, DependencyNodeRef To, DependencyEdgeKind Kind), PeriodScope> edges)
    {
        var ruleNext = inputs.RuleReads.ToLookup(r => r.ReadRegistryDefId, r => r.RuleRegistryDefId);
        var templateReaders = edges
            .Where(e => e.Key.Kind == DependencyEdgeKind.TemplateFormula)
            .ToLookup(e => e.Key.From.Id, e => (Template: e.Key.To.Id, Scope: e.Value));
        var methodologyReaders = edges
            .Where(e => e.Key.Kind == DependencyEdgeKind.Methodology)
            .ToLookup(e => e.Key.From.Id, e => (Methodology: e.Key.To.Id, Scope: e.Value));
        var resultReaders = edges
            .Where(e => e.Key.Kind == DependencyEdgeKind.MethodologyResult)
            .ToLookup(e => e.Key.From.Id, e => e.Key.To.Id);
        var writes = edges
            .Where(e => e.Key.Kind == DependencyEdgeKind.WritesInto)
            .ToLookup(e => e.Key.From.Id, e => e.Key.To.Id);

        var result = new Dictionary<(int Source, int Consumer, int Binding), TemplateDependency>();
        foreach (var binding in inputs.Bindings)
        {
            // Довідник зв'язки і ті, що читають його правилами (композиція), — обхід у ширину.
            var reach = new Dictionary<int, bool> { [binding.RegistryDefId] = true };
            var queue = new Queue<int>([binding.RegistryDefId]);
            while (queue.TryDequeue(out var registry))
            {
                foreach (var derived in ruleNext[registry].Where(d => reach.TryAdd(d, false)))
                {
                    queue.Enqueue(derived);
                }
            }

            foreach (var (registry, direct) in reach)
            {
                var consumers = templateReaders[registry].Select(r => (r.Template, r.Scope, Formula: true))
                    .Concat(AffectedMethodologies(methodologyReaders[registry], resultReaders)
                        .SelectMany(m => writes[m.Methodology].Select(t => (Template: t, m.Scope, Formula: false))));

                foreach (var (template, scope, formula) in consumers)
                {
                    var effective = direct ? scope : PeriodScope.All;
                    var key = (binding.SourceTemplateId, template, binding.BindingId);
                    result[key] = result.TryGetValue(key, out var known)
                        ? known with
                        {
                            Scope = known.Scope.Join(effective),
                            ViaTemplateFormula = known.ViaTemplateFormula || formula,
                            ViaMethodology = known.ViaMethodology || !formula,
                        }
                        : new TemplateDependency(
                            binding.SourceTemplateId, template, binding.BindingId, binding.RegistryDefId, effective, formula, !formula);
                }
            }
        }

        return [.. result.Values.OrderBy(d => d.SourceTemplateId).ThenBy(d => d.ConsumerTemplateId).ThenBy(d => d.BindingId)];
    }

    /// <summary>
    /// Методології, чиї результати змінюються, коли змінюється довідник: ті, що його читають, і — транзитивно — ті, що
    /// читають їхні результати (<see cref="DependencyEdgeKind.MethodologyResult"/>: скрипт чи формула беруть чужий вихід,
    /// зокрема з документа іншого шаблону). Обсяг читача результатів — <see cref="PeriodScope.All"/>: за який період
    /// він бере чужий вихід, граф не знає (консервативно).
    /// </summary>
    private static IEnumerable<(int Methodology, PeriodScope Scope)> AffectedMethodologies(
        IEnumerable<(int Methodology, PeriodScope Scope)> direct, ILookup<int, int> resultReaders)
    {
        var scopes = new Dictionary<int, PeriodScope>();
        var queue = new Queue<int>();
        foreach (var (methodology, scope) in direct)
        {
            scopes[methodology] = scopes.TryGetValue(methodology, out var known) ? known.Join(scope) : scope;
            queue.Enqueue(methodology);
        }

        var expanded = new HashSet<int>();
        while (queue.TryDequeue(out var source))
        {
            if (!expanded.Add(source))
            {
                continue;
            }

            foreach (var reader in resultReaders[source])
            {
                scopes[reader] = scopes.TryGetValue(reader, out var known) ? known.Join(PeriodScope.All) : PeriodScope.All;
                queue.Enqueue(reader);
            }
        }

        return scopes.Select(p => (p.Key, p.Value));
    }

    private static string CodeOf(DependencyGraphInputs inputs, DependencyNodeRef node)
    {
        var codes = node.Kind switch
        {
            DependencyNodeKind.Template => inputs.TemplateCodes,
            DependencyNodeKind.Registry => inputs.RegistryCodes,
            _ => inputs.MethodologyCodes,
        };

        return codes.GetValueOrDefault(node.Id, $"#{node.Id}");
    }
}
