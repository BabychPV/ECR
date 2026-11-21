// src/Ecr.Infrastructure/Jobs/RecalculationJob.Nightly.cs
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ PSS-15 (<c>D-562</c>, рішення людини 21.11): нічний перерахунок проєкту — ПОТОЧНИЙ рік (і попередній, поки триває
/// його пільговий строк <c>Project.YearGraceOffsetDays</c>, <see cref="NightlyYears"/>) і лише документи, входи яких
/// змінилися після їхнього актуального прогону.
/// </summary>
/// <remarks>
/// <para>
/// Доти нічний розклад (<c>NightlyRecalculationScheduling</c>) переписував усі відкриті періоди всіх років кожного документа
/// кожного активного проєкту, навіть коли не змінилось нічого: на ~250 документів замовника — сотні прогонів за ніч, кожен із
/// трасою, що лише витісняє попередній (PSS-01). Тепер батьківська задача розкладу (<see cref="NightlyFanOutAsync"/>) для
/// кожного документа вирішує окремо (<see cref="NightlyDecisionAsync"/>) і ставить у чергу лише тих, кого треба рахувати.
/// </para>
/// <para>
/// ⛔ Правило пропуску — консервативне («сумніваєшся — рахуй»), рівнем ДОКУМЕНТА за рік, а не періоду: документ пропускається,
/// лише коли в КОЖНОГО відкритого періоду року рішення є актуальний прогін і жодного входу не змінено після найстаршого з них
/// (мінус <see cref="NightlyInputsSkew"/>); інакше документ рахується весь (усі відкриті періоди року за зростанням, як доти).
/// Рівень документа, бо формули й скрипти читають інші періоди того самого документа (<c>[Period:-1]</c>, <c>[Period:#k]</c>,
/// якір річної розкладки): правка лютого після прогону березня — вхід березня. Перелік входів —
/// <see cref="NightlyInputChangeQuery"/>; що не видно жодному з них (збій інкрементної задачі формул після прогону області
/// аркуша, правка бази в обхід застосунку), закриває вік: прогін старший за <see cref="NightlyMaxBaselineAge"/> — рахуємо.
/// </para>
/// <para>
/// ⚠ Затверджені аркуші — як у будь-якому системному прогоні (<c>D-493</c>/<c>D-510</c>, <c>D-557</c>): нічна дитина не
/// несе <c>IncludeApproved</c>, тож затверджені пропускаються з журналом, подані перераховуються з позначкою за зміною чисел
/// (B79). Повернення затвердженого аркуша в роботу (подія з <c>FromStatus = Approved</c>) — вхід: аркуш, пропущений поки був
/// затверджений, наступної ночі отримує перерахунок.
/// </para>
/// </remarks>
public sealed partial class RecalculationJob
{
    /// <summary>Код задачі в журналі обслуговування (<c>itg.MaintenanceRun</c>): один рядок на проєкт за ніч.</summary>
    public const string NightlyMaintenanceCode = "nightly-recalculation";

    /// <summary>Ключ каталогу підсумку батьківської задачі нічного розкладу («рік, поставлено, пропущено»).</summary>
    public const string NightlyFannedOutKey = "jobs.recalcNightlyFannedOut";

    /// <summary>
    /// Запас «до» актуального прогону: зміна, датована трохи РАНІШЕ за його старт, однаково вважається новою.
    /// </summary>
    /// <remarks>
    /// ⛔ Мітки часу ставить застосунок до коміту (правка комірки, довідник), тож транзакція, що комітиться ПІСЛЯ старту
    /// прогону, має мітку, ранішу за нього, — прогін її не бачив (RCSI). ✎ Огляд M1: довгі транзакції — імпорт книги,
    /// матеріалізація збору — ставлять <c>aud.CellChange.ChangedAt</c> на ПОЧАТКУ й комітяться за хвилини, тож запас —
    /// година (з запасом і на розбіжність годинників вузлів Api/воркера). Ціна — щонайбільше одна зайва ніч для документа,
    /// який правили в останню годину перед його прогоном: наступної ночі актуальний прогін пізніший за правку на години.
    /// </remarks>
    public static readonly TimeSpan NightlyInputsSkew = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Найстарший актуальний прогін, який ще дозволяє пропуск: старший — документ рахується, навіть якщо входи «не змінились».
    /// </summary>
    /// <remarks>
    /// Страховка від входів, яких перелік <see cref="NightlyInputChangeQuery"/> не бачить (збій інкрементної задачі формул після
    /// прогону області аркуша, правка бази в обхід застосунку, логіка, що залежить від дати): щонайбільше 1–2 тижні
    /// застарілості, а повний нічний перерахунок кожного документа — раз на 8–14 ночей замість щоночі.
    /// <para>
    /// ✎ Огляд M2(c): межа документа — <see cref="NightlyMaxBaselineAgeOf"/> = 7 днів + (<c>Id % 7</c>) днів. Без розкиду
    /// перша (повна) ніч після оновлення повторювалась би для ВСІХ документів разом кожну восьму ніч.
    /// </para>
    /// <para>
    /// ✎ Огляд M3: вік міряється від <c>StartedAt</c> прогону, а не від <c>COALESCE(InputsAsOfUtc, StartedAt)</c>: прогін, що
    /// переніс результати затвердженого аркуша (<c>InputsAsOfUtc</c> далеко в минулому), інакше «старів» щоночі. Межа змін
    /// <c>since</c> — і далі від <c>COALESCE</c> (перенесені числа бачили довідник старого прогону).
    /// </para>
    /// </remarks>
    public static readonly TimeSpan NightlyMaxBaselineAge = TimeSpan.FromDays(7);

    /// <summary>Межа віку актуального прогону документа: <see cref="NightlyMaxBaselineAge"/> + (<c>Id % 7</c>) днів.</summary>
    /// <param name="documentId">Документ.</param>
    public static TimeSpan NightlyMaxBaselineAgeOf(long documentId)
        => NightlyMaxBaselineAge + TimeSpan.FromDays((int)(Math.Abs(documentId) % 7));

    /// <summary>Стеля документів-джерел (B179) у перевірці одного споживача; більше — «невідомо», документ рахується.</summary>
    private const int MaxNightlySourceDocuments = 500;

    /// <summary>Стеля версій шаблону документа в перевірці (поза нею — «невідомо», документ рахується).</summary>
    private const int MaxNightlyTemplateVersions = 64;

    /// <summary>
    /// ✎ Огляд L1: ціль черги документної задачі нічного розкладу — <c>doc{id}-y{year}-nightly</c>.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="year">Рік розкладу.</param>
    /// <remarks>
    /// Власна, а не <c>YearTargetOf</c> (<c>doc{id}-year</c>, діти перерахунку проєкту без року — інший обсяг, можливе
    /// перекриття затверджених) і не <c>YearScopedTargetOf</c> (кнопка «увесь рік» людини): злиття з тією, що чекає, лишає
    /// payload наявної задачі, тож нічна задача під чужою ціллю отримала б чужий обсяг чи <c>IncludeApproved</c>. Зливаються
    /// лише нічні задачі того самого документа й року (повторний запуск, поки перша ще в черзі).
    /// </remarks>
    public static string NightlyTargetOf(long documentId, int year)
        => string.Create(CultureInfo.InvariantCulture, $"doc{documentId}-y{year}-nightly");

    /// <summary>Поточний рік проєкту: календарний рік <paramref name="utcNow"/> у поясі майданчика проєкту.</summary>
    /// <param name="timeZoneId">Пояс проєкту (IANA, <c>Project.TimeZoneId</c>).</param>
    /// <param name="utcNow">Момент запуску (UTC).</param>
    public static int NightlyYear(string timeZoneId, DateTime utcNow)
        => NightlyToday(timeZoneId, utcNow).Year;

    /// <summary>
    /// ✎ PSS-15 (рішення людини 21.11, уточнення обсягу): роки нічного розкладу проєкту за зростанням — поточний рік і, поки
    /// триває річний пільговий строк, попередній.
    /// </summary>
    /// <param name="timeZoneId">Пояс проєкту (IANA, <c>Project.TimeZoneId</c>).</param>
    /// <param name="utcNow">Момент запуску (UTC).</param>
    /// <param name="yearGraceOffsetDays"><c>Project.YearGraceOffsetDays</c> (з <c>PeriodPolicy</c>, типово 45).</param>
    /// <remarks>
    /// Попередній рік — у обсязі, поки <c>сьогодні ≤ 31.12 попереднього року + YearGraceOffsetDays</c> (дати в поясі проєкту,
    /// без часу): дані року ще правлять, тож і перераховувати їх треба. Той самий відлік, що у вікні року
    /// (<see cref="Domain.Services.YearGraceWindow"/>: «+45 до 31.12.2026» — останній день 14.02.2027 включно); нуль днів —
    /// лише поточний рік.
    /// </remarks>
    public static IReadOnlyList<int> NightlyYears(string timeZoneId, DateTime utcNow, int yearGraceOffsetDays)
    {
        var today = NightlyToday(timeZoneId, utcNow);
        var previousYearEnd = new DateOnly(today.Year - 1, 12, 31);
        return today <= previousYearEnd.AddDays(Math.Max(0, yearGraceOffsetDays))
            ? [today.Year - 1, today.Year]
            : [today.Year];
    }

    private static DateOnly NightlyToday(string timeZoneId, DateTime utcNow)
    {
        var utc = utcNow.Kind == DateTimeKind.Utc ? utcNow : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
        return DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(utc, SiteTimeZone.Create(timeZoneId).ToTimeZoneInfo()));
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Нічний перерахунок проєкту {ProjectId} за рік(и) {Years}: поставлено документних задач {Queued}, пропущено "
                  + "незмінних {Skipped}, без відкритих періодів року {NoScope}.")]
    private static partial void LogNightlyFannedOut(ILogger logger, int projectId, string years, int queued, int skipped, int noScope);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Нічний перерахунок проєкту {ProjectId}: рішення про документ {DocumentId} не вдалося — документ рахується "
                  + "(причина decisionError); решта розкладу йде далі.")]
    private static partial void LogNightlyDecisionFailed(ILogger logger, int projectId, long documentId, Exception exception);

    /// <summary>
    /// Лише тести: виклик на початку рішення про документ (кинути — змоделювати збій рішення, огляд H2).
    /// </summary>
    internal Func<RecalculationRequest, Task>? NightlyDecisionProbe { get; init; }

    /// <summary>Поточний рік проєкту для нічного завдання без року (шлях без планувальника).</summary>
    private async Task<int?> NightlyYearOfProjectAsync(int projectId, CancellationToken ct)
    {
        var zone = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.TimeZoneId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return zone is null ? null : NightlyYear(zone, clock.UtcNow);
    }

    /// <summary>
    /// Нічний розклад проєкту: документні задачі поточного року (і попереднього в пільговому строку, <see cref="NightlyYears"/>)
    /// й лише для документів, які треба рахувати; рішення — на документ і рік.
    /// </summary>
    /// <param name="scheduler">Планувальник.</param>
    /// <param name="request">Завдання розкладу (<see cref="RecalculationRequest.Nightly"/>) з визначеним <c>ProjectId</c>.</param>
    /// <param name="progress">Канал прогресу батьківської задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Дитина — СИСТЕМНА робота черги: без автора й з позначкою батька (<see cref="RecalculationRequest.FanOutParentJobId"/>)
    /// ЗАВЖДИ, навіть коли канал прогресу не знає свого ідентифікатора: стеля дітей розкладу (<c>D-561</c>,
    /// <c>Jobs:Recalculation:FanOutMaxConcurrency</c>) і резерв останнього місця виконавця для ручних і авто тримаються саме на
    /// цій позначці (черга в базі — <c>FanOutParentJobId IS NOT NULL</c>, Quartz — <see cref="IsFanOutChild"/>).
    /// <para>
    /// Підсумок — і в рядку задачі (<see cref="NightlyFannedOutKey"/>: сторінка задач), і в журналі обслуговування
    /// (<see cref="NightlyMaintenanceCode"/>, подробиці з причинами).
    /// </para>
    /// </remarks>
    private async Task NightlyFanOutAsync(
        IBackgroundJobScheduler scheduler,
        RecalculationRequest request,
        IJobProgress progress,
        CancellationToken ct)
    {
        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == request.ProjectId)
            .Select(p => new { p.TimeZoneId, p.PeriodStart, p.PeriodEnd, p.YearGraceOffsetDays })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // ✎ PSS-15 (уточнення людини 21.11): поточний рік і попередній, поки триває його пільговий строк
        // (Project.YearGraceOffsetDays). Явний Year у завданні — лише він (ручний запуск, тести).
        var current = project is null ? clock.UtcNow.Year : NightlyYear(project.TimeZoneId, clock.UtcNow);
        IReadOnlyList<int> window = request.Year is { } explicitYear
            ? [explicitYear]
            : project is null
                ? [current]
                : NightlyYears(project.TimeZoneId, clock.UtcNow, project.YearGraceOffsetDays);

        // Роки, яких межі проєкту не зачіпають (минулорічний проєкт після пільгового строку), — нічого рахувати, без
        // запитів по документах.
        List<int> years = project is null
            ? []
            : [.. window.Where(y => project.PeriodStart.Year <= y && project.PeriodEnd.Year >= y)];
        var parentId = (progress as IJobIdentity)?.JobId
                       ?? string.Create(CultureInfo.InvariantCulture, $"{nameof(IRecalculationJob)}-nightly-p{request.ProjectId}");

        var run = new MaintenanceRun(NightlyMaintenanceCode, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var outcome = new NightlyOutcome();

            if (years.Count > 0)
            {
                // Контекст (кеш зв'язок джерел) — на рік: документи джерела шукаються в межах року.
                var contexts = years.ToDictionary(
                    y => y, y => new NightlyContext(request.ProjectId, y, request.NightlySkipUnchanged));

                foreach (var documentId in await ProjectDocumentIdsAsync(request.ProjectId, ct).ConfigureAwait(false))
                {
                    // Рішення — на документ І рік: попередній рік у пільговому строку — окрема документна задача зі своїм
                    // Year і своєю ціллю черги (doc{id}-y{year}-nightly), тож дві задачі документа не зливаються.
                    foreach (var year in years)
                    {
                        var child = request with
                        {
                            DocumentId = documentId,
                            PeriodKey = null,
                            Year = year,
                            FanOutParentJobId = parentId,
                            TriggeredByUserId = null,
                            IncludeApproved = false,
                            OverrideUserId = null,
                            OverrideReason = null,
                        };

                        // ✎ Огляд H2: збій рішення про ОДИН документ не зриває розклад решти проєкту — документ рахується
                        // («сумніваєшся — рахуй»), причина в журналі обслуговування — decisionError.
                        NightlyDecision decision;
                        try
                        {
                            decision = await NightlyDecisionAsync(child, contexts[year], ct).ConfigureAwait(false);
                        }
#pragma warning disable CA1031 // Рішення — оптимізація: будь-який збій означає «рахувати», а не «пропустити» чи «впасти».
                        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                        {
                            if (logger is not null)
                            {
                                LogNightlyDecisionFailed(logger, request.ProjectId, documentId, ex);
                            }

                            decision = new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.DecisionError);
                        }

                        outcome.Count(year, decision);
                        if (decision.Verdict == NightlyVerdict.Archived && logger is not null)
                        {
                            LogArchivedProjectSkipped(logger, request.ProjectId, documentId);
                        }

                        if (decision.Verdict != NightlyVerdict.Recalculate)
                        {
                            continue;
                        }

                        await scheduler
                            .EnqueueCoalescedAsync<IRecalculationJob>(NightlyTargetOf(documentId, year), child, ct)
                            .ConfigureAwait(false);
                    }
                }
            }

            run.Complete(
                "Succeeded",
                outcome.ToJson(request.ProjectId, current, years, request.NightlySkipUnchanged),
                clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var yearsText = years.Count == 0
                ? current.ToString(CultureInfo.InvariantCulture)
                : string.Join(", ", years.Select(y => y.ToString(CultureInfo.InvariantCulture)));
            if (logger is not null)
            {
                LogNightlyFannedOut(logger, request.ProjectId, yearsText, outcome.Queued, outcome.Skipped, outcome.NoScope);
            }

            await progress
                .ReportAsync(
                    100,
                    JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                        NightlyFannedOutKey,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["year"] = yearsText,
                            ["count"] = outcome.Queued.ToString(CultureInfo.InvariantCulture),
                            ["skipped"] = (outcome.Skipped + outcome.NoScope).ToString(CultureInfo.InvariantCulture),
                        })),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Рішення нічного розкладу для одного документа: рахувати, пропустити (входи не змінились), нема чого (жодного відкритого
    /// періоду року) чи архів.
    /// </summary>
    /// <param name="child">Документна задача розкладу (рік — у <see cref="RecalculationRequest.Year"/>).</param>
    /// <param name="context">Спільне на проєкт за ніч.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Правило (D-562): (1) періоди — ті самі, що порахувала б дитина: <see cref="RefusedPeriodsAsync"/> +
    /// <see cref="ScopePeriodsAsync"/> року; порожньо — нема чого. (2) У кожного з них, крім закритих якорів річної розкладки
    /// (методологій за ними немає), — актуальний прогін документа (документний або всього проєкту, найбільший <c>Id</c>);
    /// бракує хоч одного — рахуємо. (3) Межа <c>since</c> — найстарший із моментів входів цих прогонів
    /// (<c>COALESCE(InputsAsOfUtc, StartedAt)</c>, як у карті застарілості) мінус <see cref="NightlyInputsSkew"/> (60 хв);
    /// найстарший <c>StartedAt</c> старший за <see cref="NightlyMaxBaselineAgeOf"/> (7–13 днів) — рахуємо. (4) Будь-яка зміна
    /// входу після <c>since</c>
    /// (<see cref="NightlyInputChangeQuery"/>, зв'язки джерел) — рахуємо; інакше — пропуск.
    /// </remarks>
    internal async Task<NightlyDecision> NightlyDecisionAsync(
        RecalculationRequest child, NightlyContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(context);

        if (NightlyDecisionProbe is { } probe)
        {
            await probe(child).ConfigureAwait(false);
        }

        var annualOnly = new Dictionary<int, IReadOnlyList<int>>();
        IReadOnlySet<int> refused;
        try
        {
            refused = await RefusedPeriodsAsync(child, ct, annualOnly).ConfigureAwait(false);
        }
        catch (Ecr.Application.Errors.BusinessRuleException archived)
            when (archived.Details is { } details
                  && details.TryGetValue("denial", out var denial)
                  && denial as string == nameof(Ecr.Application.Calculations.RecalculationWriteDenial.ProjectArchived))
        {
            return new NightlyDecision(NightlyVerdict.Archived, NightlyReason.None);
        }

        var periods = await ScopePeriodsAsync(child, refused, ct).ConfigureAwait(false);
        if (periods.Count == 0)
        {
            return new NightlyDecision(NightlyVerdict.NoOpenPeriods, NightlyReason.None);
        }

        if (!context.SkipUnchanged)
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.Forced);
        }

        var measured = periods.Where(p => !annualOnly.ContainsKey(p)).ToList();
        if (measured.Count == 0)
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.NoBaseline);
        }

        var documentId = child.DocumentId;
        var runs = await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == child.ProjectId
                        && r.Status == Domain.Entities.Calculations.CalculationRun.CurrentStatus
                        && r.PeriodKey != null
                        && measured.Contains(r.PeriodKey.Value)
                        && (r.DocumentId == null || r.DocumentId == documentId))
            .OrderBy(r => r.PeriodKey)
            .ThenByDescending(r => r.Id)
            .Select(r => new { Period = r.PeriodKey!.Value, r.StartedAt, Inputs = r.InputsAsOfUtc ?? r.StartedAt })
            .Take(MaxPeriods * 4)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Найбільший Id на період — перший у порядку вище.
        var baseline = runs.GroupBy(r => r.Period).ToDictionary(g => g.Key, g => g.First());
        if (measured.Exists(p => !baseline.ContainsKey(p)))
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.NoBaseline);
        }

        // ✎ Огляд M3: вік — від старту прогону (перенесення результатів затвердженого аркуша не «старить» його щоночі);
        // M2(c): межа з розкидом за документом.
        var oldestStart = DateTime.SpecifyKind(baseline.Values.Min(b => b.StartedAt), DateTimeKind.Utc);
        if (clock.UtcNow - oldestStart > NightlyMaxBaselineAgeOf(documentId))
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.BaselineAged);
        }

        var since = DateTime.SpecifyKind(baseline.Values.Min(b => b.Inputs), DateTimeKind.Utc) - NightlyInputsSkew;

        // Версії шаблону документа (структура, формули, прив'язки цих версій) і шаблони — для зв'язок джерел.
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
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.Configuration);
        }

        var sources = new List<NightlySource>();
        foreach (var templateId in versions.Select(v => v.TemplateId).Distinct())
        {
            sources.AddRange(await NightlySourcesOfAsync(templateId, context, ct).ConfigureAwait(false));
        }

        if (sources.Exists(s => s.UpdatedAt is { } updated && updated >= since))
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.Source);
        }

        var sourceDocuments = sources.SelectMany(s => s.DocumentIds).Where(id => id != documentId).Distinct().ToList();
        if (sources.Exists(s => s.Overflow) || sourceDocuments.Count > MaxNightlySourceDocuments)
        {
            return new NightlyDecision(NightlyVerdict.Recalculate, NightlyReason.Source);
        }

        // ⚠ Версії ДОКУМЕНТА (за екземплярами), не Project.TemplateVersionId (B74, зникає в P6): записи журналу періодів, що
        // пишуться під версією проєкту, ловить і так гілка станів періодів (doc.Period.StateChangedAt).
        int[] templateVersionIds = [.. versions.Select(v => v.TemplateVersionId).Distinct()];
        var change = await NightlyInputChangeQuery
            .FirstChangeAsync(
                db,
                new NightlyInputProbe(child.ProjectId, documentId, since, templateVersionIds, [.. sourceDocuments]),
                ct)
            .ConfigureAwait(false);

        return change is { } reason
            ? new NightlyDecision(NightlyVerdict.Recalculate, reason)
            : new NightlyDecision(NightlyVerdict.Unchanged, NightlyReason.None);
    }

    /// <summary>
    /// Зв'язки «документ-джерело → споживач» (<c>cfg.DocumentSourceBinding</c>, читання з документа ввімкнене), де шаблон
    /// <paramref name="consumerTemplateId"/> — споживач, з документами джерела в поточному році (і попередньому: читання
    /// минулого місяця з січня).
    /// </summary>
    /// <remarks>
    /// Кеш на шаблон за ніч (<see cref="NightlyContext.SourcesByConsumer"/>): документи одного шаблону — один запит зв'язок;
    /// шаблон без зв'язок — один запит і порожньо. Документи джерела — за екземплярами таблиць версій шаблону-джерела (як у
    /// хуку джерела, <c>SourceDocumentChangeHook</c>); понад стелю — «переповнено», і споживач рахується.
    /// </remarks>
    private async Task<IReadOnlyList<NightlySource>> NightlySourcesOfAsync(
        int consumerTemplateId, NightlyContext context, CancellationToken ct)
    {
        if (context.SourcesByConsumer.TryGetValue(consumerTemplateId, out var cached))
        {
            return cached;
        }

        var bindings = await db.DocumentSourceBindings
            .AsNoTracking()
            .Where(b => b.ReadFromDocument && b.Consumers.Any(c => c.TemplateId == consumerTemplateId))
            .OrderBy(b => b.Id)
            .Select(b => new { b.SourceTemplateId, b.UpdatedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var fromKey = ((context.Year - 1) * 100) + 1;
        var toKey = (context.Year * 100) + 99;
        var result = new List<NightlySource>(bindings.Count);
        foreach (var binding in bindings)
        {
            var documents = await (
                    from i in db.TableInstances.AsNoTracking()
                    where i.PeriodKeyValue >= fromKey && i.PeriodKeyValue <= toKey
                    join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                    join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                    join v in db.TemplateVersions.AsNoTracking() on s.TemplateVersionId equals v.Id
                    where v.TemplateId == binding.SourceTemplateId
                    select i.DocumentId)
                .Distinct()
                .OrderBy(id => id)
                .Take(MaxNightlySourceDocuments + 1)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            result.Add(new NightlySource(
                binding.UpdatedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
                documents,
                documents.Count > MaxNightlySourceDocuments));
        }

        context.SourcesByConsumer[consumerTemplateId] = result;
        return result;
    }

    /// <summary>Лічильники нічного розкладу проєкту — для журналу обслуговування і рядка задачі.</summary>
    private sealed class NightlyOutcome
    {
        private readonly SortedDictionary<string, int> reasons = new(StringComparer.Ordinal);

        private readonly SortedDictionary<string, int[]> byYear = new(StringComparer.Ordinal);

        public int Queued { get; private set; }

        public int Skipped { get; private set; }

        public int NoScope { get; private set; }

        public int Archived { get; private set; }

        public void Count(int year, NightlyDecision decision)
        {
            var yearKey = year.ToString(CultureInfo.InvariantCulture);
            if (!byYear.TryGetValue(yearKey, out var counts))
            {
                byYear[yearKey] = counts = new int[4];
            }

            counts[(int)decision.Verdict]++;
            switch (decision.Verdict)
            {
                case NightlyVerdict.Recalculate:
                    Queued++;
                    var key = JsonNamingPolicy.CamelCase.ConvertName(decision.Reason.ToString());
                    reasons[key] = reasons.GetValueOrDefault(key) + 1;
                    break;
                case NightlyVerdict.Unchanged:
                    Skipped++;
                    break;
                case NightlyVerdict.NoOpenPeriods:
                    NoScope++;
                    break;
                default:
                    Archived++;
                    break;
            }
        }

        /// <summary>
        /// Підсумок: <c>year</c> — поточний рік проєкту, <c>years</c> — роки розкладу (з попереднім у пільговому строку),
        /// лічильники — документні рішення за всі роки разом, <c>byYear</c> — розбивка за роком.
        /// </summary>
        public string ToJson(int projectId, int year, IReadOnlyList<int> years, bool skipUnchanged)
            => JsonSerializer.Serialize(new
            {
                projectId,
                year,
                years,
                skipUnchanged,
                queued = Queued,
                skippedUnchanged = Skipped,
                noOpenPeriods = NoScope,
                archived = Archived,
                reasons,
                byYear = byYear.ToDictionary(
                    p => p.Key,
                    p => new
                    {
                        queued = p.Value[(int)NightlyVerdict.Recalculate],
                        skippedUnchanged = p.Value[(int)NightlyVerdict.Unchanged],
                        noOpenPeriods = p.Value[(int)NightlyVerdict.NoOpenPeriods],
                        archived = p.Value[(int)NightlyVerdict.Archived],
                    },
                    StringComparer.Ordinal),
            });
    }
}

/// <summary>✎ PSS-15: що нічний розклад вирішив про документ.</summary>
public enum NightlyVerdict
{
    /// <summary>Поставити документну задачу (усі відкриті періоди року рішення).</summary>
    Recalculate,

    /// <summary>Входи не змінились після актуального прогону кожного відкритого періоду — пропуск.</summary>
    Unchanged,

    /// <summary>У році рішення немає відкритого періоду (усе закрито, затверджено, або документ без року).</summary>
    NoOpenPeriods,

    /// <summary>Проєкт архівований чи архівується (B69/B81) — архівні дані не перераховуються.</summary>
    Archived,
}

/// <summary>✎ PSS-15: чому документ рахується (перше знайдене; журнал обслуговування, <c>reasons</c>).</summary>
public enum NightlyReason
{
    /// <summary>Не рахується.</summary>
    None = 0,

    /// <summary>Правка комірки (будь-яке походження, крім перерахунку й подій помилки формули) чи рядка документа.</summary>
    CellEdits = 1,

    /// <summary>Нові чи змінені екземпляри таблиць документа (перенос версії шаблону, нова таблиця).</summary>
    Instances = 2,

    /// <summary>Дані довідника (<c>cfg.RegistryDef.DataChangedAt</c>) — будь-якого: використання довідників транзитивне.</summary>
    Registry = 3,

    /// <summary>Публікація версії методології чи шаблону, перемикання рушія (<c>aud.PublicationEvent</c>).</summary>
    Publication = 4,

    /// <summary>
    /// Зміна конфігурації (<c>aud.StructureChange</c>): формули, структура версій документа, прив'язки методологій, довідники,
    /// періоди проєкту; або версій документа понад стелю перевірки.
    /// </summary>
    Configuration = 5,

    /// <summary>Перехід стану періоду проєкту (зокрема Reopen) чи виняток «місяць відкрито для документа».</summary>
    PeriodState = 6,

    /// <summary>Затверджений аркуш повернуто в роботу (подія з <c>FromStatus = Approved</c>).</summary>
    Unapproved = 7,

    /// <summary>Документ-джерело (B179) змінено, перераховано чи змінено його погодження; змінено саму зв'язку.</summary>
    Source = 8,

    /// <summary>
    /// ✎ Огляд H1: документ торкнуто (<c>doc.Document.ModifiedAt</c>) — шапка, склад копій аркушів, видалення рядка синком подій:
    /// зміни, яких немає в журналах правок комірок і рядків.
    /// </summary>
    Document = 9,

    /// <summary>✎ Огляд H2: рішення про документ впало винятком — документ рахується (див. журнал застосунку).</summary>
    DecisionError = 23,

    /// <summary>Нема з чим порівнювати: у відкритого періоду немає актуального прогону документа.</summary>
    NoBaseline = 20,

    /// <summary>Актуальний прогін старший за <see cref="RecalculationJob.NightlyMaxBaselineAge"/>.</summary>
    BaselineAged = 21,

    /// <summary>Пропуск незмінних вимкнено (<c>Jobs:NightlyRecalculation:SkipUnchanged = false</c>).</summary>
    Forced = 22,
}

/// <summary>✎ PSS-15: рішення про документ і його причина.</summary>
/// <param name="Verdict">Що робити.</param>
/// <param name="Reason">Чому рахувати; <see cref="NightlyReason.None"/> — не рахувати.</param>
public sealed record NightlyDecision(NightlyVerdict Verdict, NightlyReason Reason);

/// <summary>✎ PSS-15: спільне для всіх документів проєкту за ніч.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Year">Рік рішення (поточний чи попередній у пільговому строку).</param>
/// <param name="SkipUnchanged">Пропускати незмінні.</param>
internal sealed record NightlyContext(int ProjectId, int Year, bool SkipUnchanged)
{
    /// <summary>Зв'язки джерел за шаблоном-споживачем (кеш на ніч).</summary>
    public Dictionary<int, IReadOnlyList<NightlySource>> SourcesByConsumer { get; } = [];
}

/// <summary>✎ PSS-15: зв'язка документа-джерела для перевірки споживача.</summary>
/// <param name="UpdatedAt">Коли змінено стан чи біти зв'язки.</param>
/// <param name="DocumentIds">Документи шаблону-джерела.</param>
/// <param name="Overflow">Документів понад стелю — перелік неповний.</param>
internal sealed record NightlySource(DateTime? UpdatedAt, IReadOnlyList<long> DocumentIds, bool Overflow);
