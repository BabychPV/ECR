// src/Ecr.Infrastructure/Jobs/RecalculationJob.Forecast.cs
using Ecr.Application.Errors;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>Чому каскад споживача рахує період (прогноз, <see cref="ISourceCascadePlanner"/>).</summary>
internal enum CascadePlanReason
{
    /// <summary>Період, до якого зміну джерела довело відображення зв'язки (<see cref="SourceCascadeRouting"/>).</summary>
    Target,

    /// <summary>Пізніший місяць, чиї формули читають обчислені числа ранішого місяця плану (<c>[Period:-k]</c>, <c>[Period:#k]</c>, місячна колонка).</summary>
    CrossMonth,

    /// <summary>Закритий якір річної розкладки — лише не-місячні цілі (річні значення).</summary>
    AnnualAnchor,

    /// <summary>Прохід читача: скрипт методології періоду читає результати/формули місяця плану (<c>D-568</c>).</summary>
    ReaderPass,
}

/// <summary>Період, який порахує каскад споживача.</summary>
/// <param name="PeriodKey">Період.</param>
/// <param name="Reason">Чому.</param>
/// <param name="ReadsPeriodKey">Для <see cref="CascadePlanReason.CrossMonth"/> — найраніший місяць плану, який він читає.</param>
internal sealed record CascadePlannedPeriod(int PeriodKey, CascadePlanReason Reason, int? ReadsPeriodKey);

/// <summary>
/// Прогноз каскаду документа-споживача (<c>D-566</c>) тим самим кодом, що його виконує: оцінка впливу джерела показує людині
/// саме те, що порахує задача.
/// </summary>
internal interface ISourceCascadePlanner
{
    /// <summary>
    /// Що порахує задача каскаду <c>doc{id}-y{рік}-dependents</c> з періодами <paramref name="cascade"/>: план першого кроку
    /// (<c>PlanChainAsync</c> → <c>CascadeMonthsAsync</c>: періоди каскаду, у які можна писати, закриті якорі й транзитивно
    /// пізніші читачі) і проходи читачів результатів поза планом (<c>EnqueueOwnerPeriodReadersAsync</c>).
    /// </summary>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="year">Рік.</param>
    /// <param name="cascade">Періоди каскаду (з відображення зв'язки).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<IReadOnlyList<CascadePlannedPeriod>> PlanSourceCascadeAsync(
        int projectId, long documentId, int year, IReadOnlyCollection<int> cascade, CancellationToken ct);

    /// <summary>
    /// ✎ PERF-06b (<c>D-572</c>): чи методології й формули документа-джерела не переносять зміну місяця в інші місяці — той
    /// самий доказ, що вирішує місяці після власного перерахунку джерела.
    /// </summary>
    /// <param name="documentId">Документ-джерело.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task<bool> SourceKeepsMonthsAsync(long documentId, CancellationToken ct);

    /// <summary>
    /// ✎ D-581 (огляд H2): для політики зв'язки D-554 — які періоди документа за рік (БУДЬ-ЯКОГО стану, з екземплярами) читають,
    /// транзитивно, обчислені числа періодів <c>Seeds</c> (те саме правило міжмісячних читань, що в плані каскаду). Затверджений
    /// чи закритий такий читач каскадом не перераховується — і застаріє. Пакетом: звернень на виклик — сталі (екземпляри, проєкти,
    /// якорі) плюс одне на НАБІР версій шаблону.
    /// </summary>
    /// <param name="groups">Документ, рік і періоди, чиї числа змінюються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Читач (документ, період) → найраніший період, який він читає.</returns>
    public Task<IReadOnlyDictionary<(long DocumentId, int PeriodKey), int>> StaleReadersAsync(
        IReadOnlyCollection<(long DocumentId, int Year, IReadOnlyCollection<int> Seeds)> groups, CancellationToken ct);
}

/// <summary>Прогноз каскаду (<see cref="ISourceCascadePlanner"/>).</summary>
public sealed partial class RecalculationJob : ISourceCascadePlanner
{
    /// <inheritdoc />
    async Task<IReadOnlyList<CascadePlannedPeriod>> ISourceCascadePlanner.PlanSourceCascadeAsync(
        int projectId, long documentId, int year, IReadOnlyCollection<int> cascade, CancellationToken ct)
    {
        // Той самий запит, що ставить CalculationTrigger.RequestSourceCascadeAsync (без затравок «з затвердженими»).
        var request = new RecalculationRequest(projectId, documentId, PeriodKey: null, TriggeredByUserId: null)
        {
            Year = year,
            RecalculateSubmitted = true,
            SourceHop = 1,
            CascadePeriods = [.. cascade.Where(p => p / 100 == year).Distinct().Order()],
        };

        var annualOnly = new Dictionary<int, IReadOnlyList<int>>();
        IReadOnlySet<int> refused;
        try
        {
            refused = await RefusedPeriodsAsync(request, ct, annualOnly, []).ConfigureAwait(false);
        }
        catch (BusinessRuleException)
        {
            // Архівований проєкт: задача відмовила б цілим прогоном — нічого не рахується.
            return [];
        }

        var open = await ScopePeriodsAsync(request, refused, ct).ConfigureAwait(false);
        if (open.Count == 0)
        {
            return [];
        }

        var plan = Planned(open, await PlanChainAsync(request, open, annualOnly, ct).ConfigureAwait(false));
        var reads = await CrossReadsAsync(documentId, ct).ConfigureAwait(false);
        var result = new List<CascadePlannedPeriod>();
        foreach (var period in plan)
        {
            if (request.CascadePeriods.Contains(period))
            {
                result.Add(new CascadePlannedPeriod(period, CascadePlanReason.Target, null));
            }
            else if (annualOnly.ContainsKey(period))
            {
                result.Add(new CascadePlannedPeriod(period, CascadePlanReason.AnnualAnchor, null));
            }
            else
            {
                int? owner = plan.Where(p => p < period && reads(p, period)).Select(p => (int?)p).FirstOrDefault();
                result.Add(new CascadePlannedPeriod(period, CascadePlanReason.CrossMonth, owner));
            }
        }

        // Проходи читачів результатів поза планом — те саме правило, що EnqueueOwnerPeriodReadersAsync після кроків.
        if (monthRouter is { Enabled: true } && plan.Count > 0
            && await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is { } routing
            && monthRouter.RoutesComputedVersion(routing.TemplateVersionId))
        {
            foreach (var period in await ReaderPeriodsOfOwnersAsync(routing, [.. plan], ct).ConfigureAwait(false))
            {
                if (!plan.Contains(period)
                    && await ReaderPassSkipAsync(ReaderPassRequest(request, documentId, period), ct).ConfigureAwait(false)
                        == Application.Calculations.RecalculationWriteDenial.None)
                {
                    result.Add(new CascadePlannedPeriod(period, CascadePlanReason.ReaderPass, null));
                }
            }
        }

        return [.. result.OrderBy(r => r.PeriodKey)];
    }

    /// <inheritdoc />
    Task<bool> ISourceCascadePlanner.SourceKeepsMonthsAsync(long documentId, CancellationToken ct)
        => SourceKeepsMonthsAsync(documentId, ct);

    /// <inheritdoc />
    async Task<IReadOnlyDictionary<(long DocumentId, int PeriodKey), int>> ISourceCascadePlanner.StaleReadersAsync(
        IReadOnlyCollection<(long DocumentId, int Year, IReadOnlyCollection<int> Seeds)> groups, CancellationToken ct)
    {
        var result = new Dictionary<(long DocumentId, int PeriodKey), int>();
        if (groups.Count == 0)
        {
            return result;
        }

        var documentIds = groups.Select(g => g.DocumentId).Distinct().ToList();
        var instances = await (
                from i in db.TableInstances.AsNoTracking()
                where documentIds.Contains(i.DocumentId) && i.SheetCopyId == null
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                select new { i.DocumentId, i.PeriodKeyValue, sh.TemplateVersionId })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        var projects = await db.Documents.AsNoTracking()
            .Where(d => documentIds.Contains(d.Id))
            .Select(d => new { d.Id, d.ProjectId })
            .ToDictionaryAsync(d => d.Id, d => d.ProjectId, ct).ConfigureAwait(false);
        var projectIds = projects.Values.Distinct().Where(id => !_yearAnchors.ContainsKey(id)).ToList();
        if (projectIds.Count > 0)
        {
            foreach (var project in (await db.Periods.AsNoTracking()
                         .Where(p => projectIds.Contains(p.ProjectId))
                         .Select(p => new { p.ProjectId, p.PeriodKeyValue })
                         .ToListAsync(ct).ConfigureAwait(false))
                     .GroupBy(p => p.ProjectId))
            {
                _yearAnchors[project.Key] = project.GroupBy(p => p.PeriodKeyValue / 100).ToDictionary(g => g.Key, g => g.Min(p => p.PeriodKeyValue));
            }
        }

        var byDocument = instances.ToLookup(i => i.DocumentId);
        foreach (var (documentId, year, seeds) in groups)
        {
            var versions = byDocument[documentId].Select(i => i.TemplateVersionId).Distinct().ToList();
            Func<int, int, bool> reads = versions.Count > CrossReadVersionLimit
                ? static (owner, reader) => reader > owner
                : BuildReads(
                    await CrossReadRulesAsync(versions, ct).ConfigureAwait(false),
                    projects.TryGetValue(documentId, out var projectId) && _yearAnchors.TryGetValue(projectId, out var anchors) ? anchors : new Dictionary<int, int>());
            var candidates = byDocument[documentId].Select(i => i.PeriodKeyValue).Where(p => p / 100 == year).Distinct().Order().ToList();
            var sources = seeds.ToHashSet();
            foreach (var reader in candidates.Where(p => !sources.Contains(p)))
            {
                // За зростанням: кожен пізніший бачить уже доданих раніших — транзитивно за один прохід (читач завжди пізніший).
                if (sources.Where(owner => owner < reader).Order().FirstOrDefault(owner => reads(owner, reader)) is var owner and > 0)
                {
                    result[(documentId, reader)] = owner;
                    sources.Add(reader);
                }
            }
        }

        return result;
    }
}
