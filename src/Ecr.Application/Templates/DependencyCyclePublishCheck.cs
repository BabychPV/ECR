// src/Ecr.Application/Templates/DependencyCyclePublishCheck.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Expressions.Binding;

namespace Ecr.Application.Templates;

/// <summary>B179 L3 (дизайн §4): відмова публікації версії шаблону, що замкнула б цикл залежностей документів.</summary>
public static class DependencyCyclePublishCheck
{
    /// <summary>Ключ відмови «версія замкнула б цикл залежностей документів» (B179 L3).</summary>
    public const string MessageKey = "err.ECR-TMPL-0422.dependencyCycle";

    /// <summary>
    /// B179 L3: «що, якби» — поточний граф без ребер формул цього шаблону плюс ребра нової версії; новий цикл через шаблон —
    /// відмова <c>ECR-TMPL-0422</c> з шляхом кодами (шаблони й довідники за порядком).
    /// </summary>
    /// <remarks>
    /// ⚠ Граф не будується для шаблону, що не наповнює жодного довідника (єдине вихідне ребро шаблону — зв'язка джерела):
    /// для майже всіх публікацій це один запит. Довідники нової версії — ребра виду 2 щойно видобутих залежностей: за
    /// кодом (<c>RowKey</c>, RT-21) і за Lookup-колонкою (<c>REGFIELD([Lookup],…)</c>).
    /// </remarks>
    /// <param name="dependencyGraph">Граф; <c>null</c> — збирання без графа (перевірки немає).</param>
    /// <param name="version">Версія зі структурою.</param>
    /// <param name="dependencies">Щойно видобуті залежності версії.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task RefuseAsync(
        IDependencyGraphProvider? dependencyGraph, TemplateVersion version, IReadOnlyList<FormulaDependency> dependencies, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(dependencies);
        if (dependencyGraph is null
            || !await dependencyGraph.FillsRegistryAsync(version.TemplateId, ct).ConfigureAwait(false))
        {
            return;
        }

        var graph = await dependencyGraph.GetAsync(ct).ConfigureAwait(false);
        var registryByCode = graph.Nodes
            .Where(n => n.Ref.Kind == DependencyNodeKind.Registry)
            .GroupBy(n => n.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(n => n.Ref.Id), StringComparer.OrdinalIgnoreCase);
        var lookupRegistry = PublishChecks.LiveTables(version.Sheets)
            .SelectMany(t => t.Columns)
            .Where(c => c.LookupRegistryDefId is not null)
            .ToDictionary(c => c.Id, c => c.LookupRegistryDefId!.Value);

        var reads = new HashSet<int>();
        foreach (var dependency in dependencies.Where(d => d.DependsOnKind == DependencyExtractor.KindRegistry))
        {
            // Довідник без вузла в графі нічим не наповнюється й нічого не читає — цикл через нього неможливий.
            if (dependency.TableDefId is null && dependency.RowKey is { } code && registryByCode.TryGetValue(code, out var byCode))
            {
                reads.Add(byCode);
            }
            else if (dependency.ColumnDefId is { } column && lookupRegistry.TryGetValue(column, out var byLookup))
            {
                reads.Add(byLookup);
            }
        }

        if (DependencyCycleCheck.ForTemplatePublish(graph, version.TemplateId, reads) is not { } cycle)
        {
            return;
        }

        var path = DependencyCycleCheck.Describe(graph, cycle);
        throw new BusinessRuleException(
            ErrorCodes.TemplateInvalid,
            $"Публікацію відхилено: версія замкнула б цикл залежностей документів ({path}) — документ читав би сам себе.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["cycle"] = path,
            });
    }
}
