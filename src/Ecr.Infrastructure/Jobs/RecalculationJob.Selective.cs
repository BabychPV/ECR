// src/Ecr.Infrastructure/Jobs/RecalculationJob.Selective.cs
using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ PERF-01/02 (аудит PERF1000 21.11, рішення людини §6 п. 1; <c>D-564a</c>, <c>D-564b</c>): план ланцюжка перерахунку
/// документа — які місяці рахувати.
/// </summary>
/// <remarks>
/// <para>
/// <b>Кнопка «Перерахувати» документа річної розкладки</b> (<see cref="RecalculationRequest.SkipUnchanged"/>) рахує лише місяці,
/// чиї входи змінились після їхнього актуального прогону, і ЗАВЖДИ місяць на екрані; незмінні місяці лишають свій актуальний
/// прогін (підсумок <see cref="SkippedUnchangedKey"/>). «Повний перерахунок» (<see cref="RecalculationRequest.ForceFull"/>) і
/// системні ланцюжки (нічний, розклад проєкту) — усі відкриті місяці, як доти.
/// </para>
/// <para>
/// <b>Злите натискання</b> (<see cref="RecalculationRequest.ChainRecheck"/>, PERF-02) не повертає ланцюжок на перший місяць:
/// решта плану лишається, а зроблені й пропущені місяці перевіряються тим самим правилом і дописуються лише змінені.
/// </para>
/// <para>
/// ⛔ Правило «місяць змінився» (<see cref="ChangedMonthsAsync"/>) — надмножина того, що міг би змінити повний перерахунок
/// місяця; помилка тут — тихо неправильні числа, тож кожна гілка консервативна:
/// <list type="number">
///   <item>немає актуального прогону документа за місяць або новіший за нього прогін документа — <c>Failed</c>;</item>
///   <item>вхід місяця (<see cref="NightlyInputChangeQuery.MonthCellChangesAsync"/>): правка комірки ПЕРІОДУ місяця, не-місячної
///   комірки якоря (загальні дані року), будь-якого рядка документа — від <c>COALESCE(InputsAsOfUtc, StartedAt)</c> мінус
///   <see cref="NightlyInputsSkew"/> (той самий запас, що в D-562);</item>
///   <item>похідні комірки місяця (чи не-місячні якоря) переписав ХТОСЬ ІНШИЙ після його прогону — каскад формул з іншого
///   місяця (<c>[Period:-1]</c>, <c>[Period:#k]</c>, читання введених місячних колонок, річні підсумки якоря): міжперіодну
///   залежність, що вже спрацювала, доводять дані;</item>
///   <item>документні входи D-562 (екземпляри, довідники, публікації, журнал структури, стани періодів і винятки місяця,
///   повернення затвердженого аркуша, документи-джерела, дотик документа) — тим самим запитом
///   (<see cref="NightlyInputChangeQuery.FirstChangeAsync(Persistence.EcrDbContext, NightlyInputProbe, bool, CancellationToken)"/>
///   без гілки комірок);</item>
///   <item>читач результатів іншого місяця (колонка <c>Calculated</c> з чужим власником, L2b/MS), чий власник має новіший
///   актуальний прогін, — прохід читача загубився (скасовано до старту).</item>
/// </list>
/// А в самому ланцюжку — пізніші місяці, чиї формули читають змінене кроком (<see cref="ExtendWithLaterReadersAsync"/>), і
/// проходи читачів результатів поза планом (<c>EnqueueOwnerPeriodReadersAsync</c> з рештою ПЛАНУ, як доти з рештою року).
/// </para>
/// <para>
/// ⚠ Лише ланцюжок черги в базі. Режим Quartz (одна задача на всі місяці) рахує весь рік, як доти: там правка, що прийшла
/// поруч із натисканням, могла ще не дати каскаду формул (claim не поступається задачі формул документа).
/// </para>
/// </remarks>
public sealed partial class RecalculationJob
{
    /// <summary>Підсумок задачі: місяці пропущено як незмінні.</summary>
    public const string SkippedUnchangedKey = "jobs.recalcSkippedUnchanged";

    /// <summary>Те саме з підсумком журналу аркушів (<c>{message}</c>).</summary>
    public const string SkippedUnchangedWithLogKey = "jobs.recalcSkippedUnchangedWithLog";

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RecalculationJob: документ {DocumentId}: план ланцюжка {Plan}; пропущено незмінні {Skipped}; дописано злиттям {Appended} (PERF-01/02, D-564a/D-564b).")]
    private static partial void LogChainPlan(ILogger logger, long documentId, string plan, string skipped, string appended);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RecalculationJob: документ {DocumentId}: ланцюжок завершено; незмінні місяці лишили актуальний прогін: {Skipped}.")]
    private static partial void LogSkippedUnchanged(ILogger logger, long documentId, string skipped);

    /// <summary>Рішення плану ланцюжка.</summary>
    /// <param name="Plan">Періоди до розрахунку (з цим кроком), за зростанням.</param>
    /// <param name="Skipped">Відкриті місяці, пропущені як незмінні.</param>
    internal sealed record ChainPlanDecision(IReadOnlyList<int> Plan, IReadOnlyList<int> Skipped);

    /// <summary>Відкриті періоди <paramref name="open"/>, що є в плані, за зростанням.</summary>
    private static IReadOnlyList<int> Planned(IReadOnlyList<int> open, ChainPlanDecision decision)
        => [.. open.Where(decision.Plan.Contains)];

    /// <summary>План кроку ланцюжка: перше рішення, решта плану або перевірка злиття.</summary>
    /// <param name="request">Завдання документа (кроку).</param>
    /// <param name="open">Відкриті періоди скоупу ланцюжка за зростанням (для плану — усі відкриті року).</param>
    /// <param name="annualOnly">Закриті якорі річної розкладки (лише не-місячні цілі).</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<ChainPlanDecision> PlanChainAsync(
        RecalculationRequest request, IReadOnlyList<int> open, IReadOnlyDictionary<int, IReadOnlyList<int>> annualOnly,
        CancellationToken ct)
    {
        ChainPlanDecision decision;
        IReadOnlyList<int> appended = [];
        if (request.ChainAfter is null)
        {
            if (!request.SkipUnchanged || request.ForceFull)
            {
                return new ChainPlanDecision(open, []);
            }

            var flagged = await ChangedMonthsAsync(request, open, open, request.ScreenPeriods ?? [], annualOnly, ct)
                .ConfigureAwait(false);
            decision = new ChainPlanDecision([.. open.Where(flagged.Contains)], [.. open.Where(p => !flagged.Contains(p))]);
        }
        else
        {
            IReadOnlyList<int> rest = request.ChainPlan is { } planned
                ? [.. open.Where(planned.Contains)]
                : [.. open.Where(p => p > request.ChainAfter)];
            IReadOnlyList<int> skipped = [.. (request.ChainSkipped ?? []).Where(open.Contains)];
            if (!request.ChainRecheck)
            {
                return new ChainPlanDecision(rest, skipped);
            }

            // ✎ PERF-02: злите натискання — зроблені й пропущені місяці тим самим правилом; місяць на екрані — завжди, якщо його
            // ще не зроблено (зроблений і незмінний повтору не потребує: його прогін уже бачив входи натискання).
            var done = (request.ChainDone ?? []).ToHashSet();
            var candidates = open.Where(p => !rest.Contains(p)).ToList();
            var forced = (request.ScreenPeriods ?? []).Where(p => !done.Contains(p)).ToList();
            var flagged = await ChangedMonthsAsync(request, candidates, open, forced, annualOnly, ct).ConfigureAwait(false);
            appended = [.. candidates.Where(flagged.Contains)];
            decision = new ChainPlanDecision(
                [.. rest.Union(appended).Order()],
                [.. skipped.Where(p => !appended.Contains(p))]);
        }

        if (logger is not null && logger.IsEnabled(LogLevel.Information))
        {
            var planText = string.Join(',', decision.Plan);
            var skippedText = string.Join(',', decision.Skipped);
            var appendedText = string.Join(',', appended);
            LogChainPlan(logger, request.DocumentId, planText, skippedText, appendedText);
        }

        return decision;
    }

    /// <summary>
    /// Місяці з <paramref name="candidates"/>, які треба рахувати (див. правило в описі класу).
    /// </summary>
    /// <param name="request">Завдання документа.</param>
    /// <param name="candidates">Місяці, про які вирішуємо.</param>
    /// <param name="yearOpen">Усі відкриті місяці скоупу (власники результатів для гілки читачів).</param>
    /// <param name="forced">Місяці, що рахуються завжди (місяць на екрані).</param>
    /// <param name="annualOnly">Закриті якорі — лише формули (дешево): рахуються завжди.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<HashSet<int>> ChangedMonthsAsync(
        RecalculationRequest request, IReadOnlyList<int> candidates, IReadOnlyList<int> yearOpen, IEnumerable<int> forced,
        IReadOnlyDictionary<int, IReadOnlyList<int>> annualOnly, CancellationToken ct)
    {
        var forcedSet = forced.ToHashSet();
        var flagged = candidates.Where(p => forcedSet.Contains(p) || annualOnly.ContainsKey(p)).ToHashSet();
        var measured = candidates.Where(p => !flagged.Contains(p)).ToList();
        if (measured.Count == 0)
        {
            return flagged;
        }

        var documentId = request.DocumentId;
        var projectId = request.ProjectId > 0
            ? request.ProjectId
            : await db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.ProjectId).FirstAsync(ct)
                .ConfigureAwait(false);
        var keys = yearOpen.Union(candidates).ToList();

        // (1) Актуальний прогін місяця (документний або всього проєкту — найбільший Id) і новіший провалений документа.
        var runs = await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && r.PeriodKey != null
                        && keys.Contains(r.PeriodKey.Value)
                        && (r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus
                            || (r.Status == "Failed" && r.DocumentId == documentId))
                        && (r.DocumentId == null || r.DocumentId == documentId))
            .Select(r => new
            {
                r.Id,
                Period = r.PeriodKey!.Value,
                r.Status,
                r.StartedAt,
                r.FinishedAt,
                Inputs = r.InputsAsOfUtc ?? r.StartedAt,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var current = runs
            .Where(r => r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .GroupBy(r => r.Period)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!);

        foreach (var month in measured)
        {
            if (!current.TryGetValue(month, out var run)
                || runs.Exists(r => r.Period == month && r.Status == "Failed" && r.Id > run.Id))
            {
                flagged.Add(month);
            }
        }

        var open = measured.Where(p => !flagged.Contains(p)).ToList();
        if (open.Count == 0)
        {
            return flagged;
        }

        DateTime Since(int month) => DateTime.SpecifyKind(current[month].Inputs, DateTimeKind.Utc) - NightlyInputsSkew;

        // (2)+(3) Входи місяця і похідні, переписані після прогону.
        var year = request.Year ?? open[0] / 100;
        var anchor = await db.Periods
                         .AsNoTracking()
                         .Where(p => p.ProjectId == projectId && p.PeriodKeyValue > year * 100 && p.PeriodKeyValue <= (year * 100) + 99)
                         .MinAsync(p => (int?)p.PeriodKeyValue, ct)
                         .ConfigureAwait(false)
                     ?? keys.Min();
        var codes = await NightlyInputChangeQuery
            .MonthCellChangesAsync(
                db, documentId, anchor,
                [.. open.Select(m => new MonthInputProbe(
                    m, Since(m),
                    current[m].FinishedAt is { } finished ? DateTime.SpecifyKind(finished, DateTimeKind.Utc) : null))],
                ct)
            .ConfigureAwait(false);
        flagged.UnionWith(open.Where(m => codes.GetValueOrDefault(m) != 0));
        open = [.. open.Where(m => !flagged.Contains(m))];
        if (open.Count == 0)
        {
            return flagged;
        }

        // (4) Документні входи D-562: зміна в момент T будить місяці з межею ≤ T — межі за зростанням, двійковий пошук.
        var bySince = open.OrderBy(Since).ToList();
        var docLevel = await DocumentLevelProbeAsync(projectId, documentId, year, ct).ConfigureAwait(false);
        int lo = 0, hi = bySince.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (await docLevel(Since(bySince[mid])).ConfigureAwait(false))
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        flagged.UnionWith(bySince.Take(lo));
        open = [.. bySince.Skip(lo)];
        if (open.Count == 0)
        {
            return flagged;
        }

        // (5) Читач результатів місяця-власника з новішим прогоном (прохід читача загубився).
        if (monthRouter is { Enabled: true }
            && await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is { } routing
            && monthRouter.RoutesComputedVersion(routing.TemplateVersionId))
        {
            foreach (var owner in yearOpen.Where(current.ContainsKey))
            {
                if (!open.Exists(m => current[m].StartedAt < current[owner].StartedAt && m != owner))
                {
                    continue;
                }

                var readers = await ReaderPeriodsOfOwnersAsync(routing, [owner], ct).ConfigureAwait(false);
                foreach (var reader in readers.Where(r => open.Contains(r) && current[r].StartedAt < current[owner].StartedAt))
                {
                    flagged.Add(reader);
                }
            }
        }

        return flagged;
    }

    /// <summary>
    /// Перевірка документних входів D-562 (усі гілки, крім правок комірок) від межі <c>since</c>: версії шаблону документа й
    /// зв'язки документів-джерел читаються один раз.
    /// </summary>
    private async Task<Func<DateTime, Task<bool>>> DocumentLevelProbeAsync(
        int projectId, long documentId, int year, CancellationToken ct)
    {
        var versions = await (
                from i in DocumentInstancesQuery(db, documentId)
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                select new { s.TemplateVersionId, v.TemplateId })
            .Distinct()
            .OrderBy(x => x.TemplateVersionId)
            .Take(MaxNightlyTemplateVersions + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (versions.Count > MaxNightlyTemplateVersions)
        {
            return _ => Task.FromResult(true);
        }

        var context = new NightlyContext(projectId, year, SkipUnchanged: true);
        var sources = new List<NightlySource>();
        foreach (var templateId in versions.Select(v => v.TemplateId).Distinct())
        {
            sources.AddRange(await NightlySourcesOfAsync(templateId, context, ct).ConfigureAwait(false));
        }

        var sourceDocuments = sources.SelectMany(s => s.DocumentIds).Where(id => id != documentId).Distinct().ToArray();
        if (sources.Exists(s => s.Overflow) || sourceDocuments.Length > MaxNightlySourceDocuments)
        {
            return _ => Task.FromResult(true);
        }

        int[] templateVersionIds = [.. versions.Select(v => v.TemplateVersionId).Distinct()];
        return async since =>
            sources.Exists(s => s.UpdatedAt is { } updated && updated >= since)
            || await NightlyInputChangeQuery
                .FirstChangeAsync(
                    db, new NightlyInputProbe(projectId, documentId, since, templateVersionIds, sourceDocuments), cellBranch: false, ct)
                .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Пізніші за <paramref name="step"/> відкриті місяці, чиї формули читають змінене кроком (змінені комірки формул і, з
    /// запасом, усі колонки <c>Calculated</c> таблиць кроку — результати методологій кроку), дописуються в решту плану.
    /// </summary>
    /// <remarks>
    /// Лише ПІЗНІШІ: план іде за зростанням, тож дописане рахується після кроку з уже новими числами; читачі РАНІШЕ за крок
    /// (<c>[Period:#k]</c>, k пізніше) — задача формул, як і в повному ланцюжку (<c>EnqueueReadersAsync</c>). Без
    /// <see cref="CrossPeriodFanOut"/> (ручна збірка) — без доповнення.
    /// </remarks>
    private async Task<IReadOnlyList<int>> ExtendWithLaterReadersAsync(
        RecalculationRequest request, int step, IReadOnlyCollection<(int TableDefId, int ColumnDefId)> changed,
        IReadOnlyList<int> open, IReadOnlyList<int> remaining, CancellationToken ct)
    {
        if (fanOut is null)
        {
            return remaining;
        }

        var tableDefIds = await DocumentInstancesQuery(db, request.DocumentId)
            .Where(i => i.PeriodKeyValue == step)
            .Select(i => i.TableDefId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var calculated = await db.ColumnDefs
            .AsNoTracking()
            .Where(c => !c.IsDeleted && c.DataType == Domain.Enums.CellDataType.Calculated && tableDefIds.Contains(c.TableDefId))
            .Select(c => new { c.TableDefId, c.Id })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var all = changed.Union(calculated.Select(c => (c.TableDefId, c.Id))).ToList();
        if (all.Count == 0)
        {
            return remaining;
        }

        var readers = await fanOut
            .FindReadersAsync(request.DocumentId, new PeriodKey(step), all, onlyEarlierReaders: false, hops: 1, ct)
            .ConfigureAwait(false);
        var later = readers
            .Select(r => r.Reader.Value)
            .Where(r => r > step && open.Contains(r) && !remaining.Contains(r))
            .ToList();

        return later.Count == 0 ? remaining : [.. remaining.Concat(later).Distinct().Order()];
    }

    /// <summary>Підсумок «пропущено незмінні місяці» (з підсумком журналу аркушів, якщо він є).</summary>
    internal static JobProgressMessageEnvelope SkippedUnchangedEnvelope(
        IReadOnlyList<int> skipped, JobProgressMessageEnvelope? sheetLog)
        => new(
            sheetLog is null ? SkippedUnchangedKey : SkippedUnchangedWithLogKey,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = skipped.Count.ToString(CultureInfo.InvariantCulture),
                ["months"] = string.Join(
                    ", ",
                    skipped.Order().Select(p => string.Create(CultureInfo.InvariantCulture, $"{p / 100:D4}-{p % 100:D2}"))),
            },
            Inner: sheetLog);
}
