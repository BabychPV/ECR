// src/Ecr.Infrastructure/Jobs/RecalculationJob.Forecast.cs
using Ecr.Application.Errors;

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
}
