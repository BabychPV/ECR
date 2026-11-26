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

    /// <summary>Лише тести: періоди-читачі результатів власника (гілка 5) замість роутера місяців.</summary>
    internal Func<int, Task<IReadOnlyList<int>>>? OwnerReadersProbe { get; init; }

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
            if (request.CascadePeriods is { } cascade && !request.ForceFull)
            {
                // ✎ PERF-06 (D-566): каскад від правки джерела — лише періоди, що читають змінені місяці джерела, і їхні
                // пізніші читачі; решта відкритих місяців не пропускається «як незмінна» (підсумку пропуску немає).
                var touched = await CascadeMonthsAsync(request, open, cascade, annualOnly, [], request.CascadeSeeds ?? [], ct)
                    .ConfigureAwait(false);
                decision = new ChainPlanDecision([.. open.Where(touched.Contains)], []);
                LogPlan(request, decision, appended);
                return decision;
            }

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
            // ✎ Огляд F2: злита правка, усі періоди якої покриті «з затвердженими», дає порожній, але не відсутній
            // cascadeAppend — її затравки однаково будять пізніших читачів.
            var cascadeAppend = request.CascadeAppend ?? [];
            if (!request.ChainRecheck && request.CascadeAppend is null)
            {
                return new ChainPlanDecision(rest, skipped);
            }

            var candidates = open.Where(p => !rest.Contains(p)).ToList();
            if (!request.ChainRecheck)
            {
                // ✎ PERF-06 (D-566): у ланцюжок каскаду злилася нова правка джерела — її періоди дописуються ЗАВЖДИ (і вже
                // зроблені: їхній крок читав старе джерело) разом із пізнішими читачами, без детектора й без повернення на
                // перший місяць. Детектор тут хибив би в бік «увесь рік»: гілка джерел B179 у ньому — рівня документа.
                var touched = await CascadeMonthsAsync(request, candidates, cascadeAppend, annualOnly, rest, request.CascadeSeeds ?? [], ct)
                    .ConfigureAwait(false);
                appended = [.. candidates.Where(touched.Contains)];
            }
            else
            {
                // ✎ PERF-02: злите натискання — зроблені й пропущені місяці тим самим правилом; місяць на екрані — завжди, якщо
                // його ще не зроблено (зроблений і незмінний повтору не потребує: його прогін уже бачив входи натискання).
                // ✎ PERF-06: періоди каскаду, що злилися поруч, — завжди (і зроблені).
                var done = (request.ChainDone ?? []).ToHashSet();
                var forced = (request.ScreenPeriods ?? []).Where(p => !done.Contains(p)).Concat(cascadeAppend).ToList();
                var flagged = await ChangedMonthsAsync(request, candidates, open, forced, annualOnly, ct, alsoRecomputed: rest)
                    .ConfigureAwait(false);
                appended = [.. candidates.Where(flagged.Contains)];
            }

            decision = new ChainPlanDecision(
                [.. rest.Union(appended).Order()],
                [.. skipped.Where(p => !appended.Contains(p))]);
        }

        LogPlan(request, decision, appended);
        return decision;
    }

    private void LogPlan(RecalculationRequest request, ChainPlanDecision decision, IReadOnlyList<int> appended)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information))
        {
            var planText = string.Join(',', decision.Plan);
            var skippedText = string.Join(',', decision.Skipped);
            var appendedText = string.Join(',', appended);
            LogChainPlan(logger, request.DocumentId, planText, skippedText, appendedText);
        }
    }

    /// <summary>
    /// ✎ PERF-06 (<c>D-566</c>): які з <paramref name="candidates"/> рахує каскад від правки документа-джерела — періоди каскаду
    /// <paramref name="cascade"/> (їх уже звузило відображення «місяць джерела → періоди споживача»,
    /// <c>RegistryImpactRecalculationJob</c>), закриті якорі річної розкладки (лише формули, дешево — як у D-564a) і,
    /// транзитивно, пізніші місяці, що читають ОБЧИСЛЕНІ числа будь-якого з них чи місяців <paramref name="alsoRecomputed"/>
    /// (<see cref="CrossReadsAsync"/>, те саме правило, що в плані кнопки): ворота подання міжмісячної застарілості не бачать.
    /// </summary>
    /// <param name="request">Завдання документа.</param>
    /// <param name="candidates">Місяці, про які вирішуємо (відкриті, поза рештою плану).</param>
    /// <param name="cascade">Періоди каскаду.</param>
    /// <param name="annualOnly">Закриті якорі (лише не-місячні цілі).</param>
    /// <param name="alsoRecomputed">Місяці, які ланцюжок порахує й так (решта плану).</param>
    /// <param name="seeds">
    /// ✎ Огляд F2: затравки (<see cref="RecalculationRequest.CascadeSeeds"/>) — їх рахує задача «з затвердженими», а пізніших
    /// читачів їхніх чисел — ця задача; самі затравки не рахуються (хіба що вони ж читають місяць, який рахує ця).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<HashSet<int>> CascadeMonthsAsync(
        RecalculationRequest request, IReadOnlyList<int> candidates, IReadOnlyCollection<int> cascade,
        IReadOnlyDictionary<int, IReadOnlyList<int>> annualOnly, IReadOnlyCollection<int> alsoRecomputed,
        IReadOnlyCollection<int> seeds, CancellationToken ct)
    {
        var touched = candidates.Where(p => cascade.Contains(p)).ToHashSet();
        if (touched.Count == 0 && !alsoRecomputed.Any(cascade.Contains) && seeds.Count == 0)
        {
            return touched;
        }

        touched.UnionWith(candidates.Where(annualOnly.ContainsKey));
        var reads = await CrossReadsAsync(request.DocumentId, ct).ConfigureAwait(false);
        var sources = touched.Concat(alsoRecomputed).Concat(seeds).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var reader in candidates.Where(q => !touched.Contains(q)).ToList())
            {
                if (sources.Any(owner => reads(owner, reader)))
                {
                    touched.Add(reader);
                    sources.Add(reader);
                    grew = true;
                }
            }
        }

        return touched;
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
    /// <param name="alsoRecomputed">Місяці, які цей ланцюжок порахує й так (решта плану): їхні пізніші читачі — теж у план.</param>
    internal async Task<HashSet<int>> ChangedMonthsAsync(
        RecalculationRequest request, IReadOnlyList<int> candidates, IReadOnlyList<int> yearOpen, IEnumerable<int> forced,
        IReadOnlyDictionary<int, IReadOnlyList<int>> annualOnly, CancellationToken ct,
        IReadOnlyCollection<int>? alsoRecomputed = null)
    {
        var reads = await CrossReadsAsync(request.DocumentId, ct).ConfigureAwait(false);
        var flagged = await ChangedMonthsCoreAsync(request, candidates, yearOpen, forced, annualOnly, reads, ct)
            .ConfigureAwait(false);

        // ✎ Огляд H1: місяць, що рахується зараз (чи вже є в плані), міняє числа, які читають ПІЗНІШІ місяці (формули з
        // [Period:-k], [Period:#k], місячна розрахункова колонка іншого місяця) — і вони в план, транзитивно.
        var sources = flagged.Concat(alsoRecomputed ?? []).ToHashSet();
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var reader in candidates.Where(q => !flagged.Contains(q)).ToList())
            {
                if (sources.Any(owner => reads(owner, reader)))
                {
                    flagged.Add(reader);
                    sources.Add(reader);
                    grew = true;
                }
            }
        }

        return flagged;
    }

    private async Task<HashSet<int>> ChangedMonthsCoreAsync(
        RecalculationRequest request, IReadOnlyList<int> candidates, IReadOnlyList<int> yearOpen, IEnumerable<int> forced,
        IReadOnlyDictionary<int, IReadOnlyList<int>> annualOnly, Func<int, int, bool> reads, CancellationToken ct)
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
                r.DocumentId,
                r.CellChangeWatermark,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var current = runs
            .Where(r => r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus)
            .GroupBy(r => r.Period)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!);

        // ✎ Огляд H2: актуальний прогін усього ПРОЄКТУ (DocumentId IS NULL) чи прогін без межі журналу (до D-564) — межі
        // «записано після прогону» без годинників немає: місяць рахується.
        foreach (var month in measured)
        {
            if (!current.TryGetValue(month, out var run)
                || run.DocumentId is null
                || run.CellChangeWatermark is null
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
        var yearWide = await ScriptsReadMonthColumnsAsync(documentId, ct).ConfigureAwait(false);
        var codes = await NightlyInputChangeQuery
            .MonthCellChangesAsync(
                db, documentId, anchor,
                [.. open.Select(m => new MonthInputProbe(
                    m, Since(m), current[m].CellChangeWatermark!.Value,
                    DateTime.SpecifyKind(current[m].FinishedAt ?? current[m].StartedAt, DateTimeKind.Utc)))],
                ct,
                yearWideInputs: yearWide,
                yearFrom: (year * 100) + 1,
                yearTo: (year * 100) + 99)
            .ConfigureAwait(false);
        flagged.UnionWith(open.Where(m => codes.GetValueOrDefault(m) != 0));
        open = [.. open.Where(m => !flagged.Contains(m))];
        if (open.Count == 0)
        {
            return flagged;
        }

        // ✎ Огляд H1 (стан, а не пам'ять ланцюжка): місяць q читає обчислені числа ранішого місяця p, а прогін p НОВІШИЙ за
        // прогін q (порядок Id — без годинників) — q рахувався зі старих чисел p: ланцюжок, що мав дописати q, скасували чи він
        // упав, або p порахував системний прогін одного періоду (авто, залежні B179). Без цього правила q не прокинувся б ніколи.
        flagged.UnionWith(open.Where(q => yearOpen.Any(p => p < q
                                                             && current.TryGetValue(p, out var owner)
                                                             && owner.Id > current[q].Id
                                                             && reads(p, q))));
        open = [.. open.Where(m => !flagged.Contains(m))];
        if (open.Count == 0)
        {
            return flagged;
        }

        // (4) Документні входи D-562: зміна в момент T будить місяці з межею ≤ T — межі за зростанням, двійковий пошук.
        var bySince = open.OrderBy(Since).ToList();
        var (docLevel, sourceMonths) = await DocumentLevelProbeAsync(projectId, documentId, year, ct).ConfigureAwait(false);
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

        // ✎ PERF-06b (D-572): гілка джерел B179 — ПОМІСЯЧНО: місяць споживача будить лише зміна (правка, актуальний прогін,
        // погодження) періоду джерела, що покриває місяць, який він читає за обсягом ребра графа; не «усі місяці, новіші за
        // прогін джерела» (доти рівень документа будив і місяці, які каскад уже порахував).
        if (sourceMonths is not null)
        {
            var woken = await sourceMonths([.. open.Select(m => (m, Since(m)))]).ConfigureAwait(false);
            flagged.UnionWith(woken);
            open = [.. open.Where(m => !woken.Contains(m))];
            if (open.Count == 0)
            {
                return flagged;
            }
        }

        // (5) Читач результатів місяця-власника (скрипт, колонка Calculated з чужим власником) з новішим прогоном власника —
        // прохід читача загубився. Порядок прогонів — за Id (без годинників).
        Func<int, Task<IReadOnlyList<int>>>? readersOf = OwnerReadersProbe;
        if (readersOf is null
            && monthRouter is { Enabled: true }
            && await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is { } routing
            && monthRouter.RoutesComputedVersion(routing.TemplateVersionId))
        {
            readersOf = owner => ReaderPeriodsOfOwnersAsync(routing, [owner], ct);
        }

        if (readersOf is not null)
        {
            foreach (var owner in yearOpen.Where(current.ContainsKey))
            {
                if (!open.Exists(m => m != owner && current[m].Id < current[owner].Id))
                {
                    continue;
                }

                foreach (var reader in (await readersOf(owner).ConfigureAwait(false))
                             .Where(r => open.Contains(r) && current[r].Id < current[owner].Id))
                {
                    flagged.Add(reader);
                }
            }
        }

        return flagged;
    }

    /// <summary>Кеш «скрипт читає місячні колонки» за версією методології і її текстом (процес).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int VersionId, int TextHash), bool> MonthReadCache = new();

    /// <summary>
    /// ✎ PERF-01: чи хоч одна методологія-скрипт, прив'язана до таблиць документа (і бібліотеки, які вона імпортує), читає
    /// місячні колонки рядка (<c>row.X[m]</c>, <see cref="Ecr.Scripting.RowFieldInfo.IsMonthly"/>) — індекс будь-який, тож скрипт
    /// місяця m може читати ВВЕДЕНЕ в іншому місяці, і каскаду формул (гілка «похідні») такого читання не видно. Тоді вхід
    /// будь-якого місяця року — вхід кожного (консервативно, як D-562 на рівні документа).
    /// </summary>
    /// <remarks>
    /// Усі версії методології (не лише чинна на дату) і текст, що не компілюється, — «читає» (помилитися можна лише в бік
    /// «рахувати»). Формульні методології місячних колонок рахують лише свій місяць (<c>MonthColumnArgumentsModule</c>, D-545).
    /// </remarks>
    private async Task<bool> ScriptsReadMonthColumnsAsync(long documentId, CancellationToken ct)
    {
        var tableDefIds = await DocumentInstancesQuery(db, documentId)
            .Select(i => i.TableDefId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var methodologyIds = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableDefIds.Contains(b.TableDefId))
            .Select(b => b.MethodologyId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var seen = new HashSet<int>();
        var pending = methodologyIds.ToList();
        for (var depth = 0; pending.Count > 0; depth++)
        {
            // ✎ Огляд L1: імпорти глибше за 8 рівнів не розібрано — невідомо, отже «читає» (рахувати).
            if (depth >= 8)
            {
                return true;
            }

            var batch = pending.Where(seen.Add).ToList();
            pending.Clear();
            if (batch.Count == 0)
            {
                break;
            }

            var versions = await db.MethodologyVersions
                .AsNoTracking()
                .Where(v => batch.Contains(v.MethodologyId) && v.Engine == Domain.Enums.MethodologyEngine.Script)
                .Select(v => new { v.Id, v.ScriptText, v.NumericMode, Library = db.Methodologies.Any(m => m.Id == v.MethodologyId && m.Kind == Domain.Enums.MethodologyKind.Library) })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var version in versions)
            {
                var text = version.ScriptText ?? string.Empty;
                var reads = MonthReadCache.GetOrAdd((version.Id, string.GetHashCode(text, StringComparison.Ordinal)), _ =>
                {
                    var compiled = Ecr.Scripting.ScriptCompiler.Compile(
                        text, new Ecr.Scripting.ScriptCompileOptions(version.NumericMode, version.Library)).Script;
                    return compiled is null || ReadsOtherMonths(compiled);
                });
                if (reads)
                {
                    return true;
                }
            }

            var versionIds = versions.Select(v => v.Id).ToList();
            pending.AddRange(await db.MethodologyImports
                .AsNoTracking()
                .Where(i => versionIds.Contains(i.MethodologyVersionId))
                .Select(i => i.ImportedMethodologyId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        return false;
    }

    /// <summary>
    /// ✎ PERF-06b (<c>D-572</c>): доказ «зміна місяця m річного/квартального документа-джерела лишається в місяці m» — тоді
    /// каскад споживачів отримує місяці правки, а не весь рік. Кожна умова — інакше <c>false</c> (увесь рік):
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item>кожна версія кожної методології, прив'язаної до таблиць документа, — рівня <c>Module</c> (модуль місячних колонок:
    /// аргументи — комірки СВОГО місяця, D-545); інший рівень читає що завгодно;</item>
    /// <item>скрипт (і кожна бібліотека, яку він імпортує, до 8 рівнів) компілюється, не читає місячної колонки ІНШОГО місяця
    /// (<see cref="ReadsOtherMonths"/> — та сама перевірка, що D-564a п. 7 і D-568) і не читає інших таблиць
    /// (<c>ReadTables</c>);</item>
    /// <item>жодна формула версій документа не переносить місяць: зсув періоду, залежність на таблицю/діапазон без колонки,
    /// формула без колонки-цілі (рядкова), місячна колонка іншого місяця у формулі місячної, не-місячна ОБЧИСЛЮВАНА колонка у
    /// формулі місячної (річний підсумок у не-місячній колонці споживачі не читають — вони беруть лише місячні колонки й F_).
    /// Залежність формули від введеного іншого місяця, яку бачить прохід формул, однаково дала б не-місячну/іншу колонку в
    /// змінених — тут перевіряється те, що порахує лише сам прогін (результати методологій, відкладені крос-аркушні).</item>
    /// </list>
    /// Звернень: 3–4 (лише для <c>-auto</c> річного/квартального джерела з місяцями).
    /// </remarks>
    private async Task<bool> SourceKeepsMonthsAsync(long documentId, CancellationToken ct)
    {
        var instances = await (
                from i in DocumentInstancesQuery(db, documentId)
                where i.DocumentId == documentId
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                select new { i.TableDefId, sh.TemplateVersionId })
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var tableDefIds = instances.Select(i => i.TableDefId).Distinct().ToList();
        var versionIds = instances.Select(i => i.TemplateVersionId).Distinct().ToList();

        var methodologyIds = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableDefIds.Contains(b.TableDefId))
            .Select(b => b.MethodologyId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var seen = new HashSet<int>();
        var pending = methodologyIds.ToList();
        for (var depth = 0; pending.Count > 0; depth++)
        {
            if (depth >= 8)
            {
                return false;
            }

            var batch = pending.Where(seen.Add).ToList();
            pending.Clear();
            if (batch.Count == 0)
            {
                break;
            }

            var versions = await db.MethodologyVersions
                .AsNoTracking()
                .Where(v => batch.Contains(v.MethodologyId))
                .Select(v => new { v.Id, v.Engine, v.Level, v.ScriptText, v.NumericMode,
                                   Library = db.Methodologies.Any(m => m.Id == v.MethodologyId && m.Kind == Domain.Enums.MethodologyKind.Library) })
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var version in versions)
            {
                // Рівень — лише прив'язаних (бібліотека сама не рахує, її читає скрипт, що імпортує).
                if (depth == 0 && !version.Library && version.Level != Domain.Enums.CalculationLevel.Module)
                {
                    return false;
                }

                if (version.Engine != Domain.Enums.MethodologyEngine.Script)
                {
                    continue;
                }

                var compiled = Ecr.Scripting.ScriptCompiler.Compile(
                    version.ScriptText ?? string.Empty,
                    new Ecr.Scripting.ScriptCompileOptions(version.NumericMode, version.Library)).Script;
                if (compiled is null || compiled.ReadTables.Count > 0 || ReadsOtherMonths(compiled))
                {
                    return false;
                }
            }

            var scriptVersionIds = versions.Where(v => v.Engine == Domain.Enums.MethodologyEngine.Script).Select(v => v.Id).ToList();
            pending.AddRange(await db.MethodologyImports
                .AsNoTracking()
                .Where(i => scriptVersionIds.Contains(i.MethodologyVersionId))
                .Select(i => i.ImportedMethodologyId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false));
        }

        const Domain.Enums.CellDataType formula = Domain.Enums.CellDataType.Formula;
        const Domain.Enums.CellDataType calculated = Domain.Enums.CellDataType.Calculated;
        return !await (
                from d in db.FormulaDependencies.AsNoTracking()
                join f in db.FormulaDefs.AsNoTracking() on d.FormulaDefId equals f.Id
                join t in db.TableDefs.AsNoTracking() on f.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                where versionIds.Contains(sh.TemplateVersionId) && !f.IsDeleted
                join c in db.ColumnDefs.AsNoTracking() on d.ColumnDefId equals (int?)c.Id into cs
                from c in cs.DefaultIfEmpty()
                join tc in db.ColumnDefs.AsNoTracking() on f.ColumnDefId equals (int?)tc.Id into tcs
                from tc in tcs.DefaultIfEmpty()
                where (d.PeriodOffset != null && d.PeriodOffset != 0)
                      || (d.ColumnDefId == null && d.TableDefId != null)
                      || (tc == null && (d.ColumnDefId != null || d.TableDefId != null))
                      || (c != null && tc != null && c.IsMonthColumn && tc.IsMonthColumn && c.MonthNumber != tc.MonthNumber)
                      || (c != null && tc != null && !c.IsMonthColumn && tc.IsMonthColumn
                          && (c.DataType == formula || c.DataType == calculated))
                select d.FormulaDefId)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Чи скрипт читає місячну колонку з індексом, ІНШИМ за свій місяць: кожен виклик <c>$month</c>/<c>$cellMonth</c> у
    /// скомпільованому дереві, чий індекс не дослівно <c>[Period].Sequence</c> (<c>row.X[period.Month]</c>). Змінна з тим самим
    /// значенням, <c>row.X[3]</c>, <c>row.X[m-1]</c>, цикл — «читає інші» (консервативно).
    /// </summary>
    /// <param name="script">Скомпільований скрипт.</param>
    internal static bool ReadsOtherMonths(Ecr.Scripting.CompiledScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (!script.RowFields.Any(f => f.IsMonthly) && script.ReadTables.Count == 0)
        {
            return false;
        }

        var printed = new System.Text.StringBuilder();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var unknown = false;
        void Walk(object? value, int depth)
        {
            if (value is null || value is string || value.GetType().IsPrimitive || value.GetType().IsEnum
                || value is decimal or DateTime or DateOnly or TimeSpan or Guid
                || !visited.Add(value))
            {
                return;
            }

            // ✎ Огляд L1: дерево глибше за стелю — не розібране до кінця, отже «читає».
            if (depth > 64)
            {
                unknown = true;
                return;
            }

            if (value is Ecr.Expressions.Ast.AstNode node)
            {
                printed.Append(Ecr.Expressions.Ast.AstPrinter.Print(node)).AppendLine();
                return;
            }

            if (value is System.Collections.IDictionary dictionary)
            {
                foreach (var item in dictionary.Values)
                {
                    Walk(item, depth + 1);
                }

                return;
            }

            if (value is System.Collections.IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    Walk(item, depth + 1);
                }

                return;
            }

            var type = value.GetType();
            if (type.Namespace?.StartsWith("Ecr.", StringComparison.Ordinal) != true)
            {
                // ✎ Огляд L1: невідомий тип значення може ховати вираз — «читає» (рахувати), а не пропуск.
                unknown = true;
                return;
            }

            foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length == 0)
                {
                    Walk(property.GetValue(value), depth + 1);
                }
            }
        }

        Walk(script, 0);
        if (unknown)
        {
            return true;
        }

        var text = printed.ToString();
        var all = MonthCall().Count(text) + CellMonthCall().Count(text);
        var own = OwnMonthCall().Count(text) + OwnCellMonthCall().Count(text);
        return all > own;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\$month\(")]
    private static partial System.Text.RegularExpressions.Regex MonthCall();

    [System.Text.RegularExpressions.GeneratedRegex(@"\$cellMonth\(")]
    private static partial System.Text.RegularExpressions.Regex CellMonthCall();

    [System.Text.RegularExpressions.GeneratedRegex(@"\$month\('[^']*', \[Period\]\.Sequence, ")]
    private static partial System.Text.RegularExpressions.Regex OwnMonthCall();

    [System.Text.RegularExpressions.GeneratedRegex(@"\$cellMonth\('[^']*', '[^']*', \[Period\]\.Sequence\)")]
    private static partial System.Text.RegularExpressions.Regex OwnCellMonthCall();

    /// <summary>
    /// Перевірка документних входів D-562 (усі гілки, крім правок комірок) від межі <c>since</c>: версії шаблону документа й
    /// зв'язки документів-джерел читаються один раз.
    /// </summary>
    /// <remarks>
    /// ✎ PERF-06b (<c>D-572</c>): гілку документів-джерел (8) перевіряє не перша функція (рівень документа), а друга —
    /// помісячно (<see cref="SourceMonthsProbe"/>); <c>null</c> — документ не споживач. Зміна самої зв'язки (<c>UpdatedAt</c>) —
    /// рівень документа, як доти.
    /// </remarks>
    private async Task<(Func<DateTime, Task<bool>> DocumentLevel,
            Func<IReadOnlyList<(int Month, DateTime Since)>, Task<HashSet<int>>>? SourceMonths)>
        DocumentLevelProbeAsync(int projectId, long documentId, int year, CancellationToken ct)
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
            return (_ => Task.FromResult(true), null);
        }

        var context = new NightlyContext(projectId, year, SkipUnchanged: true);
        var sources = new List<(int ConsumerTemplateId, NightlySource Source)>();
        foreach (var templateId in versions.Select(v => v.TemplateId).Distinct())
        {
            sources.AddRange((await NightlySourcesOfAsync(templateId, context, ct).ConfigureAwait(false)).Select(s => (templateId, s)));
        }

        var sourceDocuments = sources.SelectMany(s => s.Source.DocumentIds).Where(id => id != documentId).Distinct().ToArray();
        if (sources.Exists(s => s.Source.Overflow) || sourceDocuments.Length > MaxNightlySourceDocuments)
        {
            return (_ => Task.FromResult(true), null);
        }

        int[] templateVersionIds = [.. versions.Select(v => v.TemplateVersionId).Distinct()];
        Func<DateTime, Task<bool>> documentLevel = async since =>
            sources.Exists(s => s.Source.UpdatedAt is { } updated && updated >= since)
            || await NightlyInputChangeQuery
                .FirstChangeAsync(
                    db, new NightlyInputProbe(projectId, documentId, since, templateVersionIds, []), cellBranch: false, ct)
                .ConfigureAwait(false) is not null;

        return (documentLevel, sourceDocuments.Length == 0
            ? null
            : months => SourceMonthsProbe(projectId, documentId, year, sources, months, ct));
    }

    /// <summary>
    /// ✎ PERF-06b (<c>D-572</c>): помісячна гілка документів-джерел B179 детектора кнопки (<c>D-564a</c> п. 5) — які з
    /// <paramref name="months"/> (місяць споживача, його межа входів) треба рахувати через зміну джерела.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Місяць споживача P читає за обсягом ребра графа зв'язки × шаблон-споживач (<see cref="Ecr.Expressions.Binding.PeriodScope"/>): «місяць» — місяці
    /// періоду P; «рік» з літеральними місяцями — ці місяці; «рік» — усі місяці року P. Покривні періоди джерела — його періоди
    /// року P, що володіють хоч одним із цих місяців (<see cref="Persistence.DocumentSourceGate.OwnedMonths"/>: місячний — свій, квартальний —
    /// три, річний — усі). P будиться, якщо після межі P у покривному періоді: правка комірки (крім записів перерахунку), новий
    /// актуальний прогін джерела (результати його методологій), подія погодження; а також правка НЕ-місячної колонки якоря року
    /// джерела в таблиці з місячними колонками (загальні дані року річної розкладки, D-540) чи додавання/видалення рядка джерела
    /// (рядок року — у всіх місяцях). Не-місячна колонка таблиці БЕЗ місячних колонок — дані свого періоду (покривний період).
    /// </para>
    /// <para>
    /// «Усе» (рік не впізнано), граф недоступний чи ребра немає — рівень документа, як доти: будь-яка зміна джерела після межі.
    /// Один запит на виклик (≤ 12 місяців × документи джерела × покривні періоди), плюс знімок графа (кеш процесу) і періоди
    /// документів джерела.
    /// </para>
    /// </remarks>
    private async Task<HashSet<int>> SourceMonthsProbe(
        int projectId, long documentId, int year, IReadOnlyList<(int ConsumerTemplateId, NightlySource Source)> sources,
        IReadOnlyList<(int Month, DateTime Since)> months, CancellationToken ct)
    {
        var woken = new HashSet<int>();
        if (months.Count == 0)
        {
            return woken;
        }

        var snapshot = dependencyGraph is null ? null : await dependencyGraph.GetAsync(ct).ConfigureAwait(false);
        var consumerKind = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.PeriodKind)
            .FirstAsync(ct).ConfigureAwait(false);

        var documentIds = sources.SelectMany(s => s.Source.DocumentIds).Where(id => id != documentId).Distinct().ToList();
        var (fromKey, toKey) = ((year * 100) + 1, (year * 100) + 99);
        var periods = (await (
                    from i in db.TableInstances.AsNoTracking()
                    where documentIds.Contains(i.DocumentId) && i.PeriodKeyValue >= fromKey && i.PeriodKeyValue <= toKey
                          && i.SheetCopyId == null
                    join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                    join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                    select new { i.DocumentId, i.PeriodKeyValue, p.PeriodKind })
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .GroupBy(x => x.DocumentId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new HashSet<(int P, DateTime Since, long Document, int? Covering, int? Anchor)>();
        foreach (var (consumerTemplateId, source) in sources)
        {
            var edges = snapshot?.TemplateDependencies
                .Where(d => d.BindingId == source.BindingId && d.ConsumerTemplateId == consumerTemplateId)
                .Select(d => d.Scope)
                .ToList();
            Ecr.Expressions.Binding.PeriodScope? scope = edges is { Count: > 0 } ? edges.Aggregate((a, b) => a.Join(b)) : null;

            foreach (var sourceDocument in source.DocumentIds.Where(id => id != documentId))
            {
                foreach (var (month, since) in months)
                {
                    if (scope is not { Kind: not Ecr.Expressions.Binding.PeriodScopeKind.All } known)
                    {
                        rows.Add((month, since, sourceDocument, null, null));
                        continue;
                    }

                    var read = known.Kind == Ecr.Expressions.Binding.PeriodScopeKind.Month
                        ? Persistence.DocumentSourceGate.OwnedMonths(consumerKind, month % 100).ToHashSet()
                        : known.MonthMask != 0
                            ? Enumerable.Range(1, 12).Where(m => (known.MonthMask & (1 << m)) != 0).ToHashSet()
                            : Enumerable.Range(1, 12).ToHashSet();
                    var own = periods.GetValueOrDefault(sourceDocument) ?? [];
                    var anchor = own.Where(p => p.PeriodKeyValue / 100 == month / 100).Select(p => (int?)p.PeriodKeyValue).Min();
                    foreach (var covering in own.Where(p => p.PeriodKeyValue / 100 == month / 100
                                                           && Persistence.DocumentSourceGate.OwnedMonths(p.PeriodKind, p.PeriodKeyValue % 100).Any(read.Contains)))
                    {
                        rows.Add((month, since, sourceDocument, covering.PeriodKeyValue, anchor));
                    }

                    // Рядки джерела (будь-який період) і не-місячні колонки якоря — дані року для кожного місяця.
                    if (anchor is { } a)
                    {
                        rows.Add((month, since, sourceDocument, 0, a));
                    }
                }
            }
        }

        if (rows.Count == 0)
        {
            return woken;
        }

        var (recalc, calcError, calcResolved) =
            (Ecr.Application.Documents.CellChangeOrigins.Recalculation, Ecr.Application.Documents.CellChangeOrigins.CalcError, Ecr.Application.Documents.CellChangeOrigins.CalcErrorResolved);
        var args = new List<object?> { recalc, calcError, calcResolved };
        var values = new System.Text.StringBuilder();
        foreach (var row in rows)
        {
            var at = args.Count;
            args.Add(row.P);
            args.Add(DateTime.SpecifyKind(row.Since, DateTimeKind.Utc));
            args.Add(row.Document);
            args.Add(row.Covering ?? -1);
            args.Add(row.Anchor ?? -1);
            values.Append(values.Length == 0 ? "(" : ", (")
                .Append(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{{{at}}}, {{{at + 1}}}, {{{at + 2}}}, {{{at + 3}}}, {{{at + 4}}})");
        }

        // K = -1 — рівень документа (будь-який період); K = 0 — лише рядки й не-місячні колонки якоря A; інакше — покривний період K.
        var sql = $$"""
            /* ecr:source-month-change */
            SELECT DISTINCT m.P AS Value
              FROM (VALUES {{values}}) AS m (P, Since, D, K, A)
              JOIN doc.Document AS sd ON sd.Id = m.D
             WHERE (m.K <> 0
                    AND (EXISTS (SELECT 1 FROM aud.CellChange AS c
                                  WHERE c.DocumentId = m.D AND (m.K = -1 OR c.PeriodKey = m.K) AND c.ChangedAt >= m.Since
                                    AND c.Origin NOT IN ({0}, {1}, {2}))
                         OR EXISTS (SELECT 1 FROM calc.CalculationRun AS r
                                     WHERE r.ProjectId = sd.ProjectId AND r.DocumentId = m.D AND (m.K = -1 OR r.PeriodKey = m.K)
                                       AND r.Status = N'Current' AND COALESCE(r.FinishedAt, r.StartedAt) >= m.Since)
                         OR EXISTS (SELECT 1 FROM wf.ApprovalEvent AS e
                                     WHERE e.DocumentId = m.D AND (m.K = -1 OR e.PeriodKey = m.K) AND e.At >= m.Since)))
                OR (m.K = 0
                    AND (EXISTS (SELECT 1 FROM aud.RowChange AS rc WHERE rc.DocumentId = m.D AND rc.ChangedAt >= m.Since)
                         OR EXISTS (SELECT 1 FROM aud.CellChange AS a
                                      JOIN cfg.ColumnDef AS cd ON cd.Id = a.ColumnDefId
                                     WHERE a.DocumentId = m.D AND a.PeriodKey = m.A AND a.ChangedAt >= m.Since
                                       AND a.Origin NOT IN ({0}, {1}, {2}) AND cd.IsMonthColumn = 0
                                       AND EXISTS (SELECT 1 FROM cfg.ColumnDef AS mc
                                                    WHERE mc.TableDefId = cd.TableDefId AND mc.IsMonthColumn = 1 AND mc.IsDeleted = 0))))
            """;
        woken.UnionWith(await db.Database
            .SqlQuery<int>(System.Runtime.CompilerServices.FormattableStringFactory.Create(sql, [.. args]))
            .OrderBy(p => p)
            .Take(months.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false));
        return woken;
    }

    /// <summary>
    /// Пізніші за <paramref name="step"/> відкриті місяці, що читають обчислені числа кроку (<see cref="CrossReadsAsync"/>), —
    /// у решту плану (те саме правило, що вирішує план, тож наступне натискання їх не «знайде» вдруге).
    /// </summary>
    /// <remarks>
    /// Лише ПІЗНІШІ: план іде за зростанням, тож дописане рахується після кроку з уже новими числами; читачі РАНІШЕ за крок
    /// (<c>[Period:#k]</c>, k пізніше) — задача формул, як і в повному ланцюжку (<c>EnqueueReadersAsync</c>).
    /// </remarks>
    private async Task<IReadOnlyList<int>> ExtendWithLaterReadersAsync(
        RecalculationRequest request, int step, IReadOnlyList<int> open, IReadOnlyList<int> remaining, CancellationToken ct)
    {
        var reads = await CrossReadsAsync(request.DocumentId, ct).ConfigureAwait(false);
        var later = open.Where(q => q > step && !remaining.Contains(q) && reads(step, q)).ToList();
        return later.Count == 0 ? remaining : [.. remaining.Concat(later).Distinct().Order()];
    }

    /// <summary>Залежності формул документа, що читають ОБЧИСЛЕНІ числа іншого місяця (кеш на виконання задачі).</summary>
    /// <remarks>Ключ — документ: прогноз каскаду (<see cref="ISourceCascadePlanner.PlanSourceCascadeAsync"/>) питає кілька документів одним екземпляром.</remarks>
    private Func<int, int, bool>? _crossReads;

    /// <summary>Документ, для якого обчислено <see cref="_crossReads"/> (ставиться разом із ним, лише після успіху).</summary>
    private long _crossReadsDocumentId;

    /// <summary>
    /// ✎ D-581 (огляд M1): залежності формул за набором версій шаблону — прогноз каскаду питає ~200 документів-споживачів
    /// одного шаблону, тож правило читається раз на набір версій, а не раз на документ.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<CrossReadDependency>?> _crossReadRules = new(StringComparer.Ordinal);

    /// <summary>✎ D-581: якорі років проєкту (найраніший місяць календаря року) — раз на проєкт.</summary>
    private readonly Dictionary<int, Dictionary<int, int>> _yearAnchors = [];

    /// <summary>Міжмісячна залежність формули (див. <see cref="CrossReadsAsync"/>).</summary>
    private sealed record CrossReadDependency(
        byte Kind, short? Offset, int? Column, bool MonthColumn, byte? Month, bool TableHasComputedMonths, bool AnnualOfRoutedTable);

    /// <summary>
    /// ✎ Огляд H1: чи місяць <c>reader</c> читає обчислені числа ранішого місяця <c>owner</c> (обидва — ключі одного року) — за
    /// залежностями формул версій шаблону документа (<c>cfg.FormulaDependency</c>), без годинника й без пам'яті ланцюжка.
    /// </summary>
    /// <remarks>
    /// <para>Читання (лише читач ПІЗНІШИЙ за власника — план іде за зростанням):</para>
    /// <list type="bullet">
    ///   <item>зсув <c>[Period:-k]</c> (будь-який вид, крім абсолютного): читач = власник + k;</item>
    ///   <item>абсолютний <c>[Period:#m]</c>: власник — місяць m, читач — будь-який пізніший;</item>
    ///   <item>місячна колонка <c>Formula</c>/<c>Calculated</c> (<c>IsMonthColumn</c>) без зсуву: власник — місяць колонки (Q-17),
    ///   читач — будь-який пізніший;</item>
    ///   <item>залежність без колонки (таблиця, діапазон) на таблицю, що має обчислювані місячні колонки, — будь-який місяць;</item>
    ///   <item>✎ повторний огляд M-a: не-місячна колонка <c>Formula</c>/<c>Calculated</c> таблиці з місячними колонками (річне
    ///   значення, власник — якір року) без зсуву: власник — якір, читач — будь-який пізніший місяць.</item>
    /// </list>
    /// <para>
    /// Лише колонки <c>Formula</c>/<c>Calculated</c> (зміну ВВЕДЕНОГО ловлять гілки входів і «похідних»). Залежність без колонки
    /// — консервативно «обчислена». Помилитися можна лише в бік «рахувати».
    /// </para>
    /// </remarks>
    private async Task<Func<int, int, bool>> CrossReadsAsync(long documentId, CancellationToken ct)
    {
        if (_crossReads is not null && _crossReadsDocumentId == documentId)
        {
            return _crossReads;
        }

        var reads = await CrossReadsCoreAsync(documentId, ct).ConfigureAwait(false);
        (_crossReads, _crossReadsDocumentId) = (reads, documentId);
        return reads;
    }

    private async Task<Func<int, int, bool>> CrossReadsCoreAsync(long documentId, CancellationToken ct)
    {
        var versionIds = await (
                from i in DocumentInstancesQuery(db, documentId)
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                select sh.TemplateVersionId)
            .Distinct()
            .OrderBy(id => id)
            .Take(CrossReadVersionLimit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ Повторний огляд M-b: версій понад стелю — перелік залежностей неповний: кожен пізніший місяць читає кожен раніший.
        if (versionIds.Count > CrossReadVersionLimit)
        {
            return static (owner, reader) => reader > owner;
        }

        // ✎ Повторний огляд M-a: якір року (найраніший місяць календаря року проєкту) — власник не-місячних колонок
        // маршрутизованих таблиць (річні значення, Q-17/D-474).
        var projectId = await db.Documents.AsNoTracking().Where(d => d.Id == documentId).Select(d => d.ProjectId)
            .FirstAsync(ct).ConfigureAwait(false);
        if (!_yearAnchors.TryGetValue(projectId, out var anchors))
        {
            anchors = (await db.Periods.AsNoTracking().Where(p => p.ProjectId == projectId).Select(p => p.PeriodKeyValue)
                    .ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(k => k / 100)
                .ToDictionary(g => g.Key, g => g.Min());
            _yearAnchors[projectId] = anchors;
        }

        var deps = await CrossReadRulesAsync(versionIds, ct).ConfigureAwait(false);
        return BuildReads(deps, anchors);
    }

    /// <summary>Правило «читач читає обчислене власника» з залежностей і якорів року (<c>null</c> — кожен пізніший читає кожен раніший).</summary>
    private static Func<int, int, bool> BuildReads(IReadOnlyList<CrossReadDependency>? deps, Dictionary<int, int> anchors)
    {
        if (deps is null)
        {
            return static (owner, reader) => reader > owner;
        }

        return (owner, reader) =>
        {
            if (reader <= owner || reader / 100 != owner / 100)
            {
                return reader > owner;
            }

            var (o, r) = (owner % 100, reader % 100);
            var isAnchor = !anchors.TryGetValue(owner / 100, out var anchor) || anchor == owner;
            return deps.Any(x =>
                (x.Kind == AbsoluteDependencyKind && x.Offset == o)
                || ((x.Offset ?? 0) == 0 && x.AnnualOfRoutedTable && isAnchor)
                || (x.Kind != AbsoluteDependencyKind && x.Offset is < 0 and var back && r + back == o)
                || ((x.Offset ?? 0) == 0 && x.MonthColumn && x.Month == o)
                || ((x.Offset ?? 0) == 0 && x.Column == null && x.TableHasComputedMonths));
        };
    }

    /// <summary>Залежності формул набору версій (кеш на екземпляр, <see cref="_crossReadRules"/>); <c>null</c> — понад стелю.</summary>
    private async Task<IReadOnlyList<CrossReadDependency>?> CrossReadRulesAsync(IReadOnlyCollection<int> versionIds, CancellationToken ct)
    {
        var ids = versionIds.Order().ToList();
        var key = string.Join(',', ids);
        if (!_crossReadRules.TryGetValue(key, out var deps))
        {
            const Domain.Enums.CellDataType formula = Domain.Enums.CellDataType.Formula;
            const Domain.Enums.CellDataType calculated = Domain.Enums.CellDataType.Calculated;
            var found = await (
                    from d in db.FormulaDependencies.AsNoTracking()
                    join f in db.FormulaDefs.AsNoTracking() on d.FormulaDefId equals f.Id
                    join t in db.TableDefs.AsNoTracking() on f.TableDefId equals t.Id
                    join sh in db.SheetDefs.AsNoTracking() on t.SheetDefId equals sh.Id
                    where ids.Contains(sh.TemplateVersionId) && !f.IsDeleted
                    join c in db.ColumnDefs.AsNoTracking() on d.ColumnDefId equals (int?)c.Id into cs
                    from c in cs.DefaultIfEmpty()
                    select new
                    {
                        Kind = d.DependsOnKind,
                        Offset = d.PeriodOffset,
                        Column = (int?)c.Id,
                        Computed = c == null || c.DataType == formula || c.DataType == calculated,
                        MonthColumn = c != null && c.IsMonthColumn,
                        Month = c == null ? null : c.MonthNumber,
                        TableHasComputedMonths = d.TableDefId != null
                            && db.ColumnDefs.Any(x => x.TableDefId == d.TableDefId && !x.IsDeleted && x.IsMonthColumn
                                                      && (x.DataType == formula || x.DataType == calculated)),
                        AnnualOfRoutedTable = c != null && !c.IsMonthColumn
                            && db.ColumnDefs.Any(x => x.TableDefId == c.TableDefId && !x.IsDeleted && x.IsMonthColumn),
                    })
                .Where(x => x.Computed && ((x.Offset != null && x.Offset != 0) || x.MonthColumn || x.AnnualOfRoutedTable
                                           || (x.Column == null && x.TableHasComputedMonths)))
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            // Понад стелю — перелік неповний: кожен пізніший місяць читає кожен раніший (консервативно; null у кеші).
            deps = found.Count > MaxCrossReads
                ? null
                : [.. found.Select(x => new CrossReadDependency(
                    x.Kind, x.Offset, x.Column, x.MonthColumn, x.Month, x.TableHasComputedMonths, x.AnnualOfRoutedTable))];
            _crossReadRules[key] = deps;
        }

        return deps;
    }

    /// <summary>Вид залежності «абсолютний період» (<c>[Period:#k]</c>), як у <c>CrossPeriodReaders</c>.</summary>
    private const byte AbsoluteDependencyKind = 5;

    /// <summary>
    /// Стеля версій шаблону документа для переліку залежностей (типово — як у нічному рішенні); понад неї — «кожен пізніший
    /// читає кожен раніший». Тести знижують її.
    /// </summary>
    internal int CrossReadVersionLimit { get; init; } = MaxNightlyTemplateVersions;

    /// <summary>Стеля переліку міжперіодних залежностей; понад неї — «кожен пізніший читає кожен раніший».</summary>
    private const int MaxCrossReads = 5000;

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
