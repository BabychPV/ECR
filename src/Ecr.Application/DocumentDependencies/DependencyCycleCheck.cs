// src/Ecr.Application/DocumentDependencies/DependencyCycleCheck.cs
namespace Ecr.Application.DocumentDependencies;

/// <summary>
/// B179 L3 (дизайн §4): «що, якби» над графом залежностей — чи замкне зміна НОВИЙ цикл через заданий вузол.
/// </summary>
/// <remarks>
/// <para>
/// Перевірка йде по сирих ребрах графа (шаблон → довідник → [правило] → шаблон / методологія → шаблон), а не по
/// згорнутих: шлях відмови тоді несе й коди довідників. Цикл рівня шаблонів через T існує тоді й лише тоді, коли в сирому
/// графі є шлях T → … → T (єдине вихідне ребро шаблону — <see cref="DependencyEdgeKind.Fills"/>).
/// </para>
/// <para>
/// ⚠ Відмова — лише на НОВИЙ цикл через вузол: якщо вузол уже лежав на циклі до зміни, зміна його не створює (виправлення
/// чи косметична публікація такого шаблону не блокуються; цикл лишається зауваженням графа й екрана L5). Цикли, що не
/// проходять через вузол, не блокують нічого.
/// </para>
/// </remarks>
public static class DependencyCycleCheck
{
    /// <summary>
    /// Публікація версії шаблону: ребра «довідник → шаблон» за формулами шаблону замінюються ребрами нової версії
    /// (ребра методологій, що пишуть у шаблон, і зв'язки шаблону лишаються).
    /// </summary>
    /// <param name="graph">Поточний граф.</param>
    /// <param name="templateId">Шаблон, версія якого публікується.</param>
    /// <param name="registryReads">Довідники, які читають формули нової версії.</param>
    /// <returns>Шлях нового циклу (від шаблону назад до нього) або <c>null</c>.</returns>
    public static IReadOnlyList<DependencyNodeRef>? ForTemplatePublish(
        DependencyGraphSnapshot graph, int templateId, IEnumerable<int> registryReads)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(registryReads);

        var template = new DependencyNodeRef(DependencyNodeKind.Template, templateId);
        var before = graph.Edges.Select(e => (e.From, e.To)).ToList();
        if (CycleThrough(before, template) is not null)
        {
            return null;
        }

        var after = graph.Edges
            .Where(e => !(e.Kind == DependencyEdgeKind.TemplateFormula && e.To == template))
            .Select(e => (e.From, e.To))
            .Concat(registryReads.Distinct().Select(r => (new DependencyNodeRef(DependencyNodeKind.Registry, r), template)))
            .ToList();
        return CycleThrough(after, template);
    }

    /// <summary>Найкоротший цикл через <paramref name="anchor"/> (обхід у ширину); <c>null</c> — немає.</summary>
    /// <param name="edges">Ребра.</param>
    /// <param name="anchor">Вузол.</param>
    /// <returns>Вузли шляху: <paramref name="anchor"/>, …, <paramref name="anchor"/>.</returns>
    public static IReadOnlyList<DependencyNodeRef>? CycleThrough(
        IEnumerable<(DependencyNodeRef From, DependencyNodeRef To)> edges, DependencyNodeRef anchor)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var next = edges.Distinct().ToLookup(e => e.From, e => e.To);
        var parent = new Dictionary<DependencyNodeRef, DependencyNodeRef>();
        var queue = new Queue<DependencyNodeRef>([anchor]);
        while (queue.TryDequeue(out var node))
        {
            foreach (var to in next[node])
            {
                if (to == anchor)
                {
                    var path = new List<DependencyNodeRef> { node };
                    for (var at = node; at != anchor; path.Add(at))
                    {
                        at = parent[at];
                    }

                    path.Reverse();
                    path.Add(anchor);
                    return path;
                }

                if (parent.TryAdd(to, node))
                {
                    queue.Enqueue(to);
                }
            }
        }

        return null;
    }

    /// <summary>Шлях кодами: <c>LAND_UI → LAND_COMPOSITION → LAND_UI</c>.</summary>
    /// <param name="graph">Граф (коди вузлів).</param>
    /// <param name="path">Шлях.</param>
    public static string Describe(DependencyGraphSnapshot graph, IReadOnlyList<DependencyNodeRef> path)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(path);

        var codes = graph.Nodes.ToDictionary(n => n.Ref, n => n.Code);
        return string.Join(" → ", path.Select(n => codes.GetValueOrDefault(n, $"#{n.Id}")));
    }
}
