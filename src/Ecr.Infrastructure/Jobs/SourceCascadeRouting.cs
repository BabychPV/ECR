// src/Ecr.Infrastructure/Jobs/SourceCascadeRouting.cs
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Expressions.Binding;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B179 L2 / PERF-06b (<c>D-566</c>, <c>D-572</c>): ЄДИНЕ відображення «зміна джерела зв'язки (рік, місяць) → документи × періоди
/// споживачів» — його виконує <see cref="RegistryImpactRecalculationJob"/> (каскад) і передбачає оцінка впливу
/// (<see cref="Persistence.SourceImpactAssessor"/>, діалог «що буде перераховано»). Одна копія правила: передбачення не
/// може розійтися з виконанням.
/// </summary>
internal static class SourceCascadeRouting
{
    /// <summary>Рішення для одного шаблону-споживача.</summary>
    /// <param name="ConsumerTemplateId">Шаблон-споживач.</param>
    /// <param name="Scope">Згорнутий обсяг ребер зв'язки до шаблону.</param>
    /// <param name="WholeYearReason">Чому увесь рік (а не власник місяця); <c>null</c> — звужено до власника місяця.</param>
    /// <param name="RoutedVersions">Версії, на яких діє звуження «рік → власник місяця»; <c>null</c> — немає.</param>
    /// <param name="Rows">Документи × періоди споживача (відкриті й пільгові).</param>
    internal sealed record ConsumerRouting(
        int ConsumerTemplateId, PeriodScope Scope, string? WholeYearReason, IReadOnlySet<int>? RoutedVersions,
        IReadOnlyList<RegistryImpactRow> Rows);

    /// <summary>Споживачі зміни джерела зв'язки <paramref name="bindingId"/> за рік і місяць (<c>null</c> — увесь рік).</summary>
    /// <param name="snapshot">Знімок графа залежностей.</param>
    /// <param name="impact">Сховище зачеплених.</param>
    /// <param name="bindingId">Зв'язка.</param>
    /// <param name="year">Рік зміни.</param>
    /// <param name="month">Місяць зміни 1–12; інше — увесь рік.</param>
    /// <param name="onCycle">Споживач у циклі з джерелом (не ставиться): шаблон-джерело, шаблон-споживач.</param>
    /// <param name="ct">Токен скасування.</param>
    internal static async Task<IReadOnlyList<ConsumerRouting>> ConsumersAsync(
        DependencyGraphSnapshot snapshot, IRegistryImpactStore impact, int bindingId, int year, int? month,
        Action<int, int>? onCycle, CancellationToken ct)
    {
        var edges = snapshot.TemplateDependencies.Where(d => d.BindingId == bindingId).ToList();
        if (edges.Count == 0)
        {
            return [];
        }

        var source = edges[0].SourceTemplateId;
        var cycle = snapshot.Cycles.FirstOrDefault(c => c.Contains(source)) ?? [];
        month = month is >= 1 and <= 12 ? month : null;

        var result = new List<ConsumerRouting>();
        foreach (var consumer in edges.GroupBy(e => e.ConsumerTemplateId))
        {
            if (cycle.Contains(consumer.Key))
            {
                onCycle?.Invoke(source, consumer.Key);
                continue;
            }

            var scope = consumer.Select(e => e.Scope).Aggregate((a, b) => a.Join(b));
            if (scope.Kind == PeriodScopeKind.Year && scope.MonthMask != 0 && month is { } m && (scope.MonthMask & (1 << m)) == 0)
            {
                continue;
            }

            var (first, last) = scope.Kind == PeriodScopeKind.All ? (int.MinValue, int.MaxValue) : (year * 100, (year * 100) + 99);
            var onlyMonth = scope.Kind == PeriodScopeKind.Month ? month : null;

            // ✎ PERF-06b (D-572): «рік з літеральними місяцями», де КОЖНЕ читання — комірка періоду-власника свого місяця
            // (TemplateDependency.MonthOwned, без методологій) — зміна місяця m будить у місячному проєкті лише період m.
            // Пізніших читачів його чисел будить прогін цього періоду (CrossPeriodFanOut, RecalculationJob.FormulasAsync).
            // ✎ W15z M1: звуження — по версії шаблону (TemplateDependency.OwnedVersions): старі версії, що читають довідник поза
            // власником, не відкочують увесь шаблон до року — лише свої періоди.
            var consumerEdges = consumer.ToList();
            var reason = scope.Kind == PeriodScopeKind.All
                ? "edge scope covers any period (derived registry or unrecognised period key)"
                : month is null
                    ? "edited months unknown (whole year)"
                    : scope.Kind == PeriodScopeKind.Month
                        ? null
                        : Persistence.MonthOwnerRouting.RefusalReason(consumerEdges);
            var monthScope = scope.Kind == PeriodScopeKind.Month;
            var routedVersions = reason is null && !monthScope ? Persistence.MonthOwnerRouting.OwnedVersions(consumerEdges) : null;
            int? routedMonth = reason is null && !monthScope ? month : null;
            var found = await impact
                .ListTemplateConsumersRoutedAsync([consumer.Key], first, last, onlyMonth, routedMonth, routedVersions, ct)
                .ConfigureAwait(false);
            result.Add(new ConsumerRouting(consumer.Key, scope, reason, routedVersions, found));
        }

        return result;
    }
}
