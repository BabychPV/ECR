using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перерахунок: інкрементний за dirty-set або повний за адміністративною
/// командою.
/// </summary>
/// <remarks>
/// **Бюджет повного річного перерахунку — ≤ 10 хвилин** (ПРД-13). Базова лінія
/// чинної системи — 20 хвилин, і формулювання «не гірше» тут не застосовується.
/// Паралельність за пакетами графа і пакетне читання входів живуть в
/// <c>CalculationOrchestrator</c>; тут — розбір завдання, життєвий цикл
/// прогону і завершення.
/// <para>
/// ⚠ Клас реалізує <see cref="IRecalculationJob"/>, а не лише
/// <c>IBackgroundJob</c>. Маркер існує саме щоб use-case міг назвати задачу,
/// не знаючи її реалізації; без нього <c>EnqueueAsync&lt;IRecalculationJob&gt;</c>
/// не мав би що запустити — черга приймала б завдання, і не робилося б нічого.
/// </para>
/// <para>
/// ⛔ Прогін складається з ДВОХ конвеєрів, і порядок між ними гарантує КОД, а
/// не порядок викликів клієнта: спершу формули шаблону (<c>doc.CellValue</c>),
/// потім методології (<c>calc.CalculationResult</c>). Причина — у тому, звідки
/// методологія бере входи: <c>CalculationInputBuilder</c> читає
/// <c>ICellStore.ReadSliceAsync</c>, тобто рівно ту таблицю, у яку пишуть
/// формули шаблону. Порахувати методології першими означало б узяти входи
/// ДО того, як вони стали правильними, — і видати новий прогін із новою
/// контрольною сумою від застарілих чисел. Неправильне число без жодної
/// ознаки неправильності (директива №10 `W10.1`).
/// </para>
/// <para>
/// ⛔ P4 ФВ-9.8 (D-206, CAL-01): перерахунок ПРОЄКТУ (<c>DocumentId &lt;= 0</c> —
/// нічний розклад, <c>RunCalculationHandler</c>) більше НЕ рахує сам, а лише
/// розкладає роботу на ДОКУМЕНТНІ задачі — по одній на документ, через
/// <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/>. Доти
/// документи йшли послідовно в одній задачі, і річний перерахунок 300 документів
/// не вкладався в 10 хвилин ПРД-13 за жодної кількості воркерів. Документна задача
/// рахує лише свій документ (усі періоди скоупу по порядку) і створює прогін
/// «документ × період», тож паралельні документи не витісняють актуальність один
/// одного (<c>UX_CalculationRun_Current</c> уже містить <c>DocumentId</c>).
/// Без планувальника (<paramref name="jobs"/> <c>null</c> — тести, утиліти) —
/// колишній послідовний шлях у цій самій задачі.
/// </para>
/// </remarks>
public sealed partial class RecalculationJob(
    EcrDbContext db,
    ICalculationRunner orchestrator,
    RunCalculationHandler runs,
    RecalculationService formulas,
    Domain.Abstractions.IClock clock,
    IBackgroundJobScheduler? jobs = null,
    RecalculationBudgetMonitor? budget = null,
    ILogger<RecalculationJob>? logger = null,
    ISheetEditGate? sheetGate = null,
    CrossPeriodFanOut? fanOut = null,
    IRecalcCommandTimeout? commandTimeout = null,
    IAuditWriter? audit = null,
    IRecalculationCompletionHook? completionHook = null,
    IMonthColumnRouter? monthRouter = null,
    IJobLeaseContext? lease = null,
    IJobQueue? queue = null) : IRecalculationJob
{
    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "RecalculationJob: перерахунок обірвано винятком; кореляція {CorrelationId}.")]
    private static partial void LogRecalculationFailed(ILogger logger, string correlationId, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: документ {DocumentId}: зупинка бази (SqlException {SqlErrorNumber}: -2 таймаут команди, "
                  + "1205 жертва дедлока); перерахунок відкладено на {Delay}. Кожне відкладення — один рядок; стеля — JobDeferral.MaxDeferral.")]
    private static partial void LogSqlStallDeferred(ILogger logger, long documentId, int sqlErrorNumber, TimeSpan delay);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: документ {DocumentId}: не вдалося позначити {Runs} прогон(ів) Failed (зупинка бази); "
                  + "початковий виняток збережено.")]
    private static partial void LogMarkFailedFailed(ILogger logger, long documentId, int runs, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RecalculationJob: документ {DocumentId}: пропущено затверджені аркуші (пар аркуш×період: {Count}).")]
    private static partial void LogSkippedSheets(ILogger logger, long documentId, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: документ {DocumentId}: через спільну методологію перераховано прив'язок поза областю/пропущених аркушів: {Count}.")]
    private static partial void LogRecomputedOutsideScope(ILogger logger, long documentId, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RecalculationJob: перерахунок проєкту {ProjectId}: проєкт архівований або архівується — документ {DocumentId} "
                  + "пропущено (B69/B81: архівні дані не перераховуються).")]
    private static partial void LogArchivedProjectSkipped(ILogger logger, int projectId, long documentId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: задачу скасовано, а {Runs} незавершений(і) прогін(и) закрити не вдалося; їх закриє AbandonedWorkSweeper.")]
    private static partial void LogCloseCancelledFailed(ILogger logger, int runs, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: задачу перервано, а позначки «перераховано після подання» ({Sheets} аркуш(ів), документ {DocumentId}) записати не вдалося.")]
    private static partial void LogFlushMarksFailed(ILogger logger, long documentId, int sheets, Exception exception);

    /// <summary>Ключ каталогу причини: прогін закрито, бо задачу скасовано (L1b / D4).</summary>
    public const string CancelledRunKey = "jobs.calculationRunCancelled";

    /// <summary>Причина <c>calc.CalculationRun.ErrorMessage</c> прогону скасованої задачі — конверт мовою читача.</summary>
    public static readonly string CancelledRunReason =
        JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(CancelledRunKey));

    /// <summary>
    /// ✎ L1b / D4: прогони, які задача створила й не завершила, — <c>Failed</c> з причиною «скасовано» одразу на скасуванні.
    /// </summary>
    /// <remarks>
    /// ⛔ Без токена задачі (він уже скасований) і лише ті, що ДОСІ <c>Running</c> у БАЗІ (стан у пам'яті міг лишитися від
    /// відкоченого перемикання, тож умова — лише в UPDATE): завершений прогін періоду вже
    /// актуальний (чи замінений) і чіпати його не можна; прогін, який паралельно закрив прибиральник, — теж. Збій тут лише в
    /// журнал: скасування не підміняється новим винятком, а завислі прогони закриє <c>AbandonedWorkSweeper</c>, як доти.
    /// </remarks>
    private async Task CloseCancelledRunsAsync(IEnumerable<Domain.Entities.Calculations.CalculationRun?> runsOfJob)
    {
        var ids = runsOfJob
            .OfType<Domain.Entities.Calculations.CalculationRun>()
            .Where(run => run.Id > 0)
            .Select(run => run.Id)
            .ToList();
        if (ids.Count == 0)
        {
            return;
        }

        try
        {
            db.ChangeTracker.Clear();
            var utcNow = clock.UtcNow;
            await db.CalculationRuns
                .Where(r => ids.Contains(r.Id) && r.Status == "Running")
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(r => r.Status, "Failed")
                        .SetProperty(r => r.FinishedAt, utcNow)
                        .SetProperty(r => r.ErrorMessage, CancelledRunReason),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Закриття — другорядне: скасування не підміняється збоєм бази.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseCancelledFailed(logger ?? NullLogger<RecalculationJob>.Instance, ids.Count, ex);
        }
    }

    /// <summary>Стеля прив'язок на прогін: методологій у системі — десятки.</summary>
    private const int MaxBindings = 5_000;

    /// <summary>Скільки шкали прогресу віддано формулам шаблону.</summary>
    /// <remarks>
    /// ⚠ Оркестратор методологій рахує власні відсотки від нуля
    /// (<c>CalculationOrchestrator</c>). Без масштабування шкала стрибала б
    /// назад — 40 %, потім знову 5 %, — і «скільки лишилося» перестало б
    /// щось означати саме тоді, коли прогін довгий і на нього дивляться.
    /// </remarks>
    private const int FormulaPhaseShare = 40;

    /// <summary>Скільки разів повторити транзакцію перемикання, коли аркуш саме подається (Y1-02).</summary>
    private const int CompletionBusyAttempts = 6;

    /// <summary>Пауза між повторами транзакції перемикання (Y1-02).</summary>
    /// <remarks>⚠ <c>init</c> — лише заради тестів: секунди очікування там не потрібні.</remarks>
    internal TimeSpan CompletionBusyDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Налаштування розбору завдання; спільні на всі виклики.</summary>
    /// <remarks>
    /// Один екземпляр на клас, а не на виклик: <c>JsonSerializerOptions</c>
    /// кешує метадані типів усередині, і новий об'єкт щоразу означає повторний
    /// розбір рефлексією на кожне завдання черги.
    /// </remarks>
    private static readonly JsonSerializerOptions PayloadOptions =
        new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var request = Parse(payload);

        // ⛔ `ProjectId` визначається з ДОКУМЕНТА, коли payload його не несе, а
        // не приймається як 0. `RecalculateDocumentHandler` кладе в чергу
        // `new { DocumentId, PeriodKey }` — без `ProjectId` узагалі; при
        // розборі в non-nullable `int` це мовчки стає `0`. `CalculationRun`
        // із `ProjectId = 0` не проходить `FK_CalculationRun_Project`, і
        // `SaveChangesAsync` нижче кидав `SqlException 547` — виміряно живим
        // прогоном (директива №09 §1.3).
        var projectId = request.ProjectId > 0
            ? request.ProjectId
            : await db.Documents
                .AsNoTracking()
                .Where(d => d.Id == request.DocumentId)
                .Select(d => d.ProjectId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        // ✎ PSS-15 (D-562): роки нічного розкладу визначаються в момент виконання (тригер ставиться на старті і живе через
        // Новий рік) і в дочірні задачі йдуть уже числом. Розклад проєкту (є планувальник) вибирає їх сам — поточний і
        // попередній у пільговому строку (NightlyYears); тут — лише шлях без розкладу: поточний рік.
        if (request.Nightly && request.PeriodKey is null && request.Year is null
            && !(request.DocumentId <= 0 && jobs is not null))
        {
            request = request with { Year = await NightlyYearOfProjectAsync(projectId, ct).ConfigureAwait(false) };
        }

        // ⛔ P4 ФВ-9.8: проєкт — лише розклад на документні задачі, без прогону тут.
        if (request.DocumentId <= 0 && jobs is not null)
        {
            if (request.Nightly)
            {
                // ✎ PSS-15: нічний розклад ставить лише документи, які треба рахувати (D-562).
                await NightlyFanOutAsync(jobs, request with { ProjectId = projectId }, progress, ct).ConfigureAwait(false);
                return;
            }

            await FanOutAsync(jobs, request with { ProjectId = projectId }, progress, ct).ConfigureAwait(false);
            return;
        }

        // ⛔ Серіалізація ЗА ДОКУМЕНТОМ (P4, борг перед I2). Річна задача
        // (`doc{id}-year`) і поперіодна (`doc{id}-p{period}`) мають різні ключі
        // цілі, тож черга пускала їх одночасно. Річна перемикає актуальність УСІХ
        // своїх прогонів у кінці: коли поперіодна (новіший прогін) завершувалась
        // раніше, річна падала на `UX_CalculationRun_Current` — порушення
        // обмеження не ретраїться, і `Failed` ставали прогони всіх її періодів
        // (`RecalculationJobDocumentSerializationTests`). Лок сесійний, на окремому
        // з'єднанні: задача живе кількома транзакціями, і транзакційний лок не
        // накрив би її цілком; черга й пул лишаються як є.
        // Таймаут команд контексту ЗАДАЧІ (запис прогону, завершення/перемикання) піднімається до стелі
        // перерахунку; гілки оркестратора піднімають свої контексти самі (`IRecalcCommandTimeout`).
        commandTimeout?.Apply();

        // ✎ PSS-04 (D-561, рішення людини 20.11): перерахунок документа за кілька періодів у черзі в базі — ЛАНЦЮЖОК кроків
        // по одному періоду: крок рахує формули й методології свого періоду, робить його актуальним (CompleteAsync),
        // звільняє лок документа й місце виконавця і продовжується наступним кроком (JobContinuedException → той самий
        // рядок черги з новим payload). Межа тривалості — на крок; між кроками йдуть інші задачі документа (правка комірки).
        // Порядок періодів — за зростанням, як і доти ([Period:-1]). Quartz і збірка без черги — як доти, одна задача.
        var claim = lease?.Current;
        var chain = request.DocumentId > 0 && request.PeriodKey is null
                    && claim is not null && queue is { SupportsContinuation: true };

        // ✎ L1b / D5 (стенд Land 21.11): прибирання прогонів, які крок зняв з актуальності (D-560, ~141 тис. рядків на
        // прогін, DELETE TOP пакетами), ішло ВСЕРЕДИНІ лока документа: +47–111 с (~40 %) на крок, і правка комірки та
        // ручний перерахунок документа чекали. Тепер перемикання лише складає їх сюди, а прибирає `purgeAfterLock` — він
        // оголошений РАНІШЕ за лок, тож звільняється ПІСЛЯ нього (зворотний порядок `await using`). У черзі в базі —
        // ще пізніше: після фіксації рядка задачі воркером (IJobLeaseContext.TryRunAfterSettle), бо до неї документ для
        // claim зайнятий (Running, PSS-04). Семантика L3 та сама: лише передані прогони, стеля швидкого шляху; нічне вікно —
        // лише накопиченому.
        var supersededRuns = new List<SupersededRun>();
        await using var purgeAfterLock = new PurgeAfterDocumentLock(runs, lease, supersededRuns, logger, ct);
        await using var documentLock = await AcquireDocumentLockAsync(request.DocumentId, ct).ConfigureAwait(false);

        // ✎ Перф-аудит 20.11 (B80 N5): «залежні з затвердженими» — надмножина звичайних залежних того самого документа й
        // періоду. Звичайна, що стала в чергу, поки ця чекала лок (чи була на мить Running у циклі claim → відкладення), —
        // постановкою не покрита; але лок уже наш і входи ще не читані, тож її робота буде зроблена тут із надлишком.
        await CancelCoveredDependentsAsync(request, ct).ConfigureAwait(false);

        // ✎ PSS-06 (D-561): той самий принцип для БУДЬ-ЯКОЇ цілі того самого документа й періоду (кнопка, область аркуша,
        // авто, залежні, прохід читача, дитина розкладу): задачі, що чекають і чию роботу цей прогін робить з надлишком,
        // паркуються ДО першого читання — і закриваються разом із ним. Названий період відомий уже тут.
        if (request.DocumentId > 0 && request.PeriodKey is { } namedForCover)
        {
            await ParkCoveredAsync(request, namedForCover, ct).ConfigureAwait(false);
        }

        // ⛔ «CalculationRun ховає результати сусідніх документів» (третя
        // хвиля UX-PASS R4): прогін ОДНОГО документа (`request.DocumentId > 0`,
        // маршрут `RecalculateDocumentHandler`) несе свій `DocumentId`, щоб
        // `SwitchCurrentRunAsync` знімав актуальність лише в межах ЦЬОГО
        // документа, а не всього `(ProjectId, PeriodKey)`. Прогін усього
        // проєкту (`DocumentId <= 0` — нічний розклад чи адміністративна
        // команда) лишається `DocumentId = null`, як і завжди: він рахує ВСІ
        // документи проєкту заново, тож законно перекриває їх усіх.
        //
        // ⛔⛔ ОДИН ПРОГІН НА ПЕРІОД, а не один на рік. Прогін «на весь рік»
        // (`PeriodKey = null`, нічний розклад) раніше створював ОДИН
        // `CalculationRun` без періоду, а оркестратор пише результати без
        // ключа періоду (`ICalculationResultStore.WriteResultsAsync`) — сховище
        // бере його з прогону, `run.PeriodKey ?? 0`. Тобто кожне число нічного
        // перерахунку лягало в «період 0», якого не читає ні
        // `ReadCurrentAsync`, ні зріз `rpt.*`: нічний перерахунок рахував і
        // не показував нічого (`RecalculationJobYearRunResultsVisibilityTests`).
        // Прогін на кожен період дає і правильну партицію результатів, і
        // правильне перемикання актуальності (`SwitchCurrentRunAsync` — за
        // `(ProjectId, PeriodKey)`): ручний прогін січня перекриває нічний
        // лише в січні, а не лишає два актуальні прогони з подвоєними рядками.
        // Модель `CalculationRun` не змінюється — `PeriodKey` лишається
        // nullable для прогону, якому немає чого рахувати (див. нижче).
        await AwaitRegistryWritersAsync(ct).ConfigureAwait(false);
        var startedAt = clock.UtcNow;
        var periodRuns = new SortedDictionary<int, (Domain.Entities.Calculations.CalculationRun Run, ModuleProfile Profile)>();
        Domain.Entities.Calculations.CalculationRun? yearRun = null;

        // RC14 (P2-4): що переносити в прогін періоду (лише для прогону області аркуша).
        var carryOvers = new Dictionary<int, List<ResultCarryOver>>();

        // UAT-recalc-ux: періоди, які прогін рахував, за документами — для журналу пропущених/перерахованих поданих.
        var scopeByDocument = new Dictionary<long, int[]>();

        // ✎ MS: затверджені аркуші читачів, яких не перераховано через затвердження, — для позначки «пропущено».
        var blockedReaders = new Dictionary<long, IReadOnlySet<(int Period, int SheetDefId)>>();

        // ✎ LAND-YEAR L2b: (документ, період-читач, періоди-власники), чиї результати читали скрипти цього періоду.
        var foreignOwnerReads = new List<(long DocumentId, int Period, IReadOnlyCollection<int> Owners)>();

        // ✎ B79: подані аркуші, у яких прогін справді змінив хоч одне число (комірки формул — зі шляху запису; результати
        // методологій — порівнянням у транзакції перемикання). Лише вони отримують позначку «перераховано після подання».
        var submittedChanges = new SubmittedSheetChanges();

        // ✎ Позначка після подання (e2e «SHEETNAME: copy marker», 21.11): журнал аркушів (SheetLogAsync) уже записав позначки
        // змінених поданих аркушів. Доти задача, перервана ПІСЛЯ запису формул (витіснення кнопкою «Перерахувати» —
        // Exclusive, межа тривалості, відкладення, збій), губила позначку назавжди: числа поданого аркуша вже інші, а
        // наступний прогін змін не бачить і позначки не ставить. Див. FlushSubmittedMarksAsync.
        var submittedMarksWritten = false;

        // D2-03: що перевірити перед перемиканням актуальності кожного періоду — аркуші, які прогін
        // ПЕРЕРАХУВАВ методологіями (за документами), і чи період ще дозволяє запис.
        var completionGuards = new Dictionary<int, List<SheetGuard>>();

        // L-4: рядки, яким не підійшло жодне правило; назовні — лише кількість і номери.
        var unmatchedRows = new List<UnmatchedRow>();
        var rejectedRows = new List<RejectedRow>();

        // AN-164/AN-180: рядки, закриті правилом, без жодного значення через відсутні входи формул.
        var missingInputRows = new List<RejectedRow>();

        // G14: методології без опублікованих версій — привʼязки пропущено, прогін не падає.
        var skippedUnpublished = new SortedSet<int>();

        // ✎ PSS-04: період цього кроку ланцюжка і ті, що лишились після нього (за зростанням).
        int? stepPeriod = null;
        IReadOnlyList<int> remaining = [];

        // ✎ PERF-01/02: відкриті періоди скоупу ланцюжка і рішення плану (одне на виконання кроку).
        IReadOnlyList<int> openPeriods = [];
        ChainPlanDecision? chainDecision = null;

        // ✎ PSS-04: прогрес кроку — частина шкали всього ланцюжка (зроблені періоди + цей + решта).
        var chainProgress = chain ? new ChainProgress(progress, request.ChainDone?.Length ?? 0, request.ChainTotal) : null;
        if (chainProgress is not null)
        {
            progress = chainProgress;
        }

        Domain.Entities.Calculations.CalculationRun NewRun(int? periodKey)
            => new(
                projectId, periodKey, request.TriggeredByUserId, startedAt,
                documentId: request.DocumentId > 0 ? request.DocumentId : null);

        async Task<(Domain.Entities.Calculations.CalculationRun Run, ModuleProfile Profile)> RunForAsync(int periodKey)
        {
            if (!periodRuns.TryGetValue(periodKey, out var entry))
            {
                entry = (NewRun(periodKey), new ModuleProfile());
                periodRuns.Add(periodKey, entry);
                db.CalculationRuns.Add(entry.Run);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return entry;
        }

        try
        {
            // ⛔ Створення й ПЕРШЕ збереження — ВСЕРЕДИНІ `try`, а не до нього.
            // Раніше стояли до `try`: коли сам `INSERT` провалювався (рівно
            // так і сталося з `ProjectId = 0` вище), виняток летів МИМО catch
            // нижче — і задача лишалася `Running` назавжди, хоча catch
            // виглядав так, ніби мав це перехопити.
            //
            // ⚠ Названий період — прогін створюється ОДРАЗУ, як і завжди.
            if (request.PeriodKey is { } requestedPeriod)
            {
                await RunForAsync(requestedPeriod).ConfigureAwait(false);
            }

            // ⛔ Q-151/Q-162 (аудит фази 1). `RunCalculationHandler` УЖЕ ставив
            // у чергу payload без `DocumentId` (нуль після розбору JSON) —
            // документація поля прямо казала «нуль — усі документи проєкту»,
            // але сюди ніхто не дійшов: фільтр нижче на `DocumentId == 0`
            // завжди повертав ПОРОЖНІЙ перелік прив'язок, а прогін завершувався
            // `Succeeded` над нулем документів. Тепер `DocumentId <= 0` справді
            // означає «усі документи проєкту» — рішення людини: «так, потрібен
            // явний маршрут».
            var documentIds = request.DocumentId > 0
                ? (IReadOnlyList<long>)[request.DocumentId]
                : await ProjectDocumentIdsAsync(projectId, ct).ConfigureAwait(false);

            for (var i = 0; i < documentIds.Count; i++)
            {
                var documentId = documentIds[i];
                var docRequest = request with { DocumentId = documentId };

                // ⛔⛔ ГЕЙТ СТАНУ ПЕРІОДУ — ТУТ, на самому шляху запису, а не
                // на HTTP-вході. До цього фіксу його на цьому шляху не було
                // ЗОВСІМ, хоча `RecalculateDocumentHandler` у власному
                // коментарі стверджував протилежне: мовляв, стан періоду
                // перевіряє `RunCalculationHandler`, «якому задача передає
                // керування». Передачі не існувало — задача кличе з нього лише
                // `CompleteAsync` (завершення прогону, де періодів немає
                // взагалі), а `RecalculationService` не мав перевірки стану
                // періоду жодної. Отже ФВ-9.7 тримався рівно на одному з трьох
                // маршрутів (перерахунок ПРОЄКТУ), а маршрут документа й нічний
                // розклад переписували числа закритих і поданих періодів мовчки
                // й «успішно».
                //
                // ⚠ Гейт свідомо стоїть у ЗАДАЧІ, а не тільки в обробниках: усі
                // три маршрути сходяться саме тут, і перевірка на кожному вході
                // окремо — це знову три копії рішення, які розійдуться.
                // Обробник документа перевіряє те саме ще й до черги, але це
                // швидка відмова заради людини (422 замість «jobId, який
                // нічого не зробить»), а не другий гейт.
                // ✎ LAND-YEAR B24 (D-NEW-3): закритий якір річної розкладки у відкритому році — НЕ відмова, а прогін лише
                // не-місячних цілей під агрегатом року (ключ — якір, значення — місяці року); методологій за ним немає.
                var annualOnly = new Dictionary<int, IReadOnlyList<int>>();
                var refused = await RefusedPeriodsAsync(docRequest, ct, annualOnly).ConfigureAwait(false);

                // Названий період, створений наперед (вище), — закритий якір: прогону методологій за ним не буде, а
                // завершити його означало б зробити порожній прогін актуальним і сховати результати закритого місяця.
                // Свіжий прогін без жодного результату прибирається (кілька документів — він міг бути вже спільним).
                if (request.PeriodKey is { } namedAnchor
                    && documentIds.Count == 1
                    && annualOnly.ContainsKey(namedAnchor)
                    && periodRuns.TryGetValue(namedAnchor, out var unusedRun))
                {
                    periodRuns.Remove(namedAnchor);
                    db.CalculationRuns.Remove(unusedRun.Run);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                // ⚠ Один документ — той самий діапазон 0…100, що й завжди
                // (i=0, Count=1 дає floor=0, ceiling=100): жодна наявна
                // поведінка не змінюється. Кілька документів ділять шкалу
                // порівну між собою.
                var floor = i * 100 / documentIds.Count;
                var ceiling = (i + 1) * 100 / documentIds.Count;
                var formulaCeiling = floor + ((ceiling - floor) * FormulaPhaseShare / 100);

                // ⛔ КРОК 1 — формули шаблону, і саме ВСЕРЕДИНІ `try`. Відмова
                // тут мусить позначити прогін `Failed` із причиною, а не
                // лишити його `Running` назавжди: рівно цей клас дефекту в
                // цьому файлі вже коштував розбору двічі (`D2-285`, `D2-286`).
                //
                // ⚠ Q-326: повідомлення прогресу — структурований конверт
                // (ключ каталогу + параметри), не готовий український текст.
                // Префікс «Документ N (i з M): » — окремий шар композиції
                // (`jobs.documentPrefix`), застосований лише коли документів
                // кілька — той самий умовний префікс, що й раніше, але тепер
                // РЕЗОЛВИТЬСЯ мовою читача при `GET /api/v1/jobs/{jobId}`, а
                // не записаний однією мовою назавжди.
                await progress
                    .ReportAsync(
                        floor,
                        JobProgressMessageCodec.Encode(WithDocumentPrefix(
                            new JobProgressMessageEnvelope("jobs.recalcFormulas"),
                            documentId, i + 1, documentIds.Count)),
                        ct)
                    .ConfigureAwait(false);

                // ⛔ Періоди, у які цей прогін МАЄ ПРАВО писати, рахуються ОДИН
                // раз і обслуговують ОБИДВА конвеєри. Раніше фаза формул
                // вираховувала їх у себе всередині, а фаза методологій не
                // вираховувала взагалі — саме звідти й узявся `PeriodKey(0)`
                // нижче (див. коментар біля виклику оркестратора).
                var periods = await ScopePeriodsAsync(docRequest, refused, ct).ConfigureAwait(false);

                // ✎ PERF-01/02 (D-564a/D-564b): план ланцюжка — які з відкритих періодів рахувати. Перший крок кнопки річного
                // документа вирішує «лише змінені + місяць на екрані»; злите натискання (ChainRecheck) дописує змінені
                // зроблені й пропущені місяці; далі — решта плану. Повний ланцюжок (нічний, розклад, «Повний») — усі.
                if (chain && periods.Count > 0)
                {
                    openPeriods = periods;
                    chainDecision ??= await PlanChainAsync(docRequest, periods, annualOnly, ct).ConfigureAwait(false);
                    periods = Planned(periods, chainDecision);
                }
                else if (!chain && periods.Count > 0 && request.CascadePeriods is not null && !request.ForceFull)
                {
                    // ✎ PERF-06 (D-566): каскад без ланцюжка (немає оренди черги в базі) — той самий план: періоди каскаду й
                    // пізніші читачі їхніх обчислених чисел, за зростанням однією задачею.
                    periods = Planned(periods, await PlanChainAsync(docRequest, periods, annualOnly, ct).ConfigureAwait(false));
                }

                // ✎ PSS-04/06: крок ланцюжка — лише ПЕРШИЙ період решти скоупу. Задачі, що чекають на нього, паркуються
                // до читання входів; якщо щось запарковано — стан періодів перечитується (рішення «що рахувати»
                // ухвалено до паркування), і якщо крок після цього інший — запарковане повертається в чергу.
                if (chain && periods.Count > 0)
                {
                    var step = periods[0];
                    if (await ParkCoveredAsync(request, step, ct).ConfigureAwait(false) > 0)
                    {
                        annualOnly.Clear();
                        refused = await RefusedPeriodsAsync(docRequest, ct, annualOnly).ConfigureAwait(false);
                        periods = await ScopePeriodsAsync(docRequest, refused, ct).ConfigureAwait(false);
                        openPeriods = periods;
                        periods = Planned(periods, chainDecision!);
                        if (periods.Count == 0 || periods[0] != step)
                        {
                            await queue!.UnparkAsync(claim!, ct).ConfigureAwait(false);
                        }
                    }

                    if (periods.Count > 0)
                    {
                        stepPeriod = periods[0];
                        remaining = [.. periods.Skip(1)];
                        periods = [periods[0]];
                        chainProgress!.Total = chainProgress.Done + periods.Count + remaining.Count;
                    }
                }

                var stepChanged = chain ? new Dictionary<int, HashSet<(int TableDefId, int ColumnDefId)>>() : null;
                var cells = await FormulasAsync(docRequest, periods, submittedChanges, ct, annualOnly, stepChanged)
                    .ConfigureAwait(false);

                // ✎ PERF-01 (D-564a, огляд H1): план вужчий за рік — пізніші місяці, що читають обчислені числа цього кроку
                // (формули з [Period:-k]/[Period:#k], місячні розрахункові колонки), дописуються в план (CrossReadsAsync).
                if (chain && stepPeriod is { } extendFrom && stepChanged is not null
                    && openPeriods.Any(p => p > extendFrom && !remaining.Contains(p)))
                {
                    remaining = await ExtendWithLaterReadersAsync(docRequest, extendFrom, openPeriods, remaining, ct)
                        .ConfigureAwait(false);
                    chainProgress!.Total = chainProgress.Done + 1 + remaining.Count;
                }

                // ✎ LAND-YEAR B24: методології закритого якоря не рахуються (результати — місяця, а він закритий).
                IReadOnlyList<int> methodologyScope = annualOnly.Count == 0
                    ? periods
                    : [.. periods.Where(p => !annualOnly.ContainsKey(p))];

                await progress
                    .ReportAsync(
                        formulaCeiling,
                        JobProgressMessageCodec.Encode(WithDocumentPrefix(
                            new JobProgressMessageEnvelope(
                                "jobs.recalcFormulasDone",
                                new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["cells"] = cells.ToString(CultureInfo.InvariantCulture),
                                }),
                            documentId, i + 1, documentIds.Count)),
                        ct)
                    .ConfigureAwait(false);

                // ⛔ RC15 (P2-A): перерахунок УСЬОГО документа пропускає ЗАТВЕРДЖЕНІ аркуші: ні їхні комірки
                // (їх не пише `RecalculationService`), ні результати методологій (прив'язки пропускаються нижче;
                // результати переносяться з попереднього прогону) не змінюються.
                // ✎ B79 (слово людини 16.11): подані аркуші перераховує КОЖЕН прогін, і нічний теж; пропуск — лише затверджені.
                // ✎ F13 p218: і для області аркуша — період із затвердженим оригіналом тепер допускається заради його
                // копій-чернеток, а методології оригіналу (його прив'язки) мусять лишитися пропущеними.
                var skippedSheets = request.IncludeApproved
                    ? new Dictionary<int, HashSet<int>>()
                    : await ApprovedSheetsAsync(docRequest, request.PeriodKey, ct).ConfigureAwait(false);
                var skippedPairs = skippedSheets.Where(pair => periods.Contains(pair.Key)).Sum(pair => pair.Value.Count);
                if (logger is not null && skippedPairs > 0)
                {
                    LogSkippedSheets(logger, documentId, skippedPairs);
                }

                var bindingPlan = await BindingsAsync(docRequest, methodologyScope, skippedSheets, ct).ConfigureAwait(false);
                var bindingsByPeriod = bindingPlan.Bindings;
                blockedReaders[documentId] = bindingPlan.BlockedReaders;
                if (logger is not null && bindingPlan.RecomputedOnInactive > 0)
                {
                    LogRecomputedOutsideScope(logger, documentId, bindingPlan.RecomputedOnInactive);
                }

                // ⛔ КРОК 2 — методології, і лише тепер: їхні входи щойно
                // стали актуальними.
                await progress
                    .ReportAsync(
                        formulaCeiling,
                        JobProgressMessageCodec.Encode(WithDocumentPrefix(
                            new JobProgressMessageEnvelope("jobs.recalcMethodologiesStart"),
                            documentId, i + 1, documentIds.Count)),
                        ct)
                    .ConfigureAwait(false);

                // ⛔ ОДИН прогін на ПЕРІОД (`RunForAsync`) на всі документи —
                // `CalculationRun` прив'язаний до проєкту й періоду
                // (`FK_CalculationRun_Project`), не до документа. Профілі модулів
                // зводяться в сумарний запис прогону періоду —
                // `ModuleProfile.Record` акумулює за кодом модуля, тож повторний
                // виклик на кожен документ саме те, для чого метод і існує.
                //
                // ⛔⛔ ПОПЕРІОДНО, а не одним викликом на весь прогін. Раніше
                // тут стояло `new PeriodKey(request.PeriodKey ?? 0)`: один
                // ключ на весь документ. Для прогону «на весь рік»
                // (`PeriodKey = null` — нічний розклад,
                // `NightlyRecalculationScheduling`) періоду в завданні немає за
                // побудовою, і `?? 0` давав `PeriodKey(0)` — ключ, який НЕ є
                // періодом (`PeriodKey.IsValid` — false, `A7-28`).
                // `CalculationOrchestrator.PeriodDateAsync` шукав межі такого
                // періоду, не знаходив і кидав `ECR-PRD-0404`. Отже нічний
                // повний перерахунок падав для БУДЬ-ЯКОГО проєкту з активними
                // прив'язками методологій — і, судячи з коду, падав завжди:
                // задача за розкладом, яка ніколи не працювала саме для того
                // випадку, заради якого існує. Порожній список прив'язок
                // рятував лише тому, що оркестратор виходить на
                // `bindings.Count == 0` ДО обчислення дати.
                //
                // ⛔ Правильна одиниця тут — ПЕРІОД, а не документ, і це не
                // судження про зручність, а вимога: версія методології
                // резолвиться ЗА ДАТОЮ ПЕРІОДУ (ФВ-9.3, `MethodologyResolver`),
                // тож рік — це потенційно різні версії в різних місяцях.
                // Порахувати весь рік з одним ключем означало б застосувати
                // до грудня редакцію методології, чинну в січні, — тихо
                // неправильне число в регульованому звіті. Той самий принцип,
                // що вже діє у фазі формул («періоди беруться з ЕКЗЕМПЛЯРІВ
                // таблиць, а не з `request.PeriodKey`») і в моделі даних:
                // `calc.CalculationResult` партиціонований ВЛАСНИМ
                // `PeriodKey`, а `CalculationRun.PeriodKey` — nullable саме
                // тому, що один прогін має право накрити весь рік.
                //
                // ⚠ Названий період дає рівно ОДИН виклик із тим самим ключем,
                // що й раніше: маршрут кнопки «Перерахувати» не змінюється ні
                // на крок. І саме тому список періодів для методологій — НЕ
                // той самий, що для формул. Названий період лишається в ньому
                // НАВІТЬ тоді, коли екземплярів таблиць у ньому немає: так цей
                // код поводився завжди (оркестратор отримував ключ завдання й
                // повертався на порожніх прив'язках), і на це спирається
                // `DocumentId_нуль_перераховує_УСІ_документи_проєкту` —
                // документ без жодного екземпляра мусить бути ВИДИМО
                // перерахованим, а не мовчки пропущеним. Вивести й цей випадок
                // з екземплярів означало б замість дефекту річного прогону
                // завести дефект «документ тихо випав із прогону».
                //
                // ⛔ Для прогону БЕЗ названого періоду такого запасного ключа
                // не існує за визначенням — там єдине джерело правди про
                // періоди це екземпляри таблиць. Підставити туди нуль і був
                // початковий дефект.
                var methodologyPeriods = request.PeriodKey is { } namedPeriod
                    ? (annualOnly.ContainsKey(namedPeriod) ? [] : (IReadOnlyList<int>)[namedPeriod])
                    : methodologyScope;
                scopeByDocument[documentId] = [.. methodologyScope.Union(methodologyPeriods)];

                // ⛔ RC14 (приймальна №10, P2-4) + RC15 (P2-C): прогін області аркуша чи документа з пропущеними
                // аркушами стає актуальним для ВСЬОГО документа й періоду, тож результати методологій, яких він
                // не рахував (прив'язані лише до таблиць інших/пропущених аркушів), переносяться в нього з
                // попереднього актуального прогону (`CompleteAsync` → `CarryOverResultsAsync`), а не зникають.
                // Перелік складає `BindingsAsync` за періодами.

                for (var p = 0; p < methodologyPeriods.Count; p++)
                {
                    var period = methodologyPeriods[p];

                    // Смуга прогресу фази методологій ділиться порівну між
                    // періодами — так само, як уся шкала ділиться між
                    // документами вище.
                    var periodFloor =
                        formulaCeiling + ((ceiling - formulaCeiling) * p / methodologyPeriods.Count);
                    var periodCeiling =
                        formulaCeiling + ((ceiling - formulaCeiling) * (p + 1) / methodologyPeriods.Count);

                    var (run, runProfile) = await RunForAsync(period).ConfigureAwait(false);

                    if (!completionGuards.TryGetValue(period, out var periodGuards))
                    {
                        completionGuards[period] = periodGuards = [];
                    }

                    periodGuards.Add(new SheetGuard(
                        documentId,
                        bindingPlan.ActiveSheets.GetValueOrDefault(period) ?? [],
                        bindingsByPeriod.GetValueOrDefault(period) ?? []));

                    if (bindingPlan.CarryOver.TryGetValue(period, out var carryMethodologies))
                    {
                        if (!carryOvers.TryGetValue(period, out var periodCarry))
                        {
                            carryOvers[period] = periodCarry = [];
                        }

                        periodCarry.Add(new ResultCarryOver(documentId, carryMethodologies));
                    }

                    // ✎ D-557: затверджені екземпляри спільних методологій — їхні числа лишаються старими.
                    if (bindingPlan.CarryOverInstances.TryGetValue(period, out var sharedMethodologies)
                        && bindingPlan.CarryOverInstanceIds.TryGetValue(period, out var sharedInstances))
                    {
                        if (!carryOvers.TryGetValue(period, out var periodCarry))
                        {
                            carryOvers[period] = periodCarry = [];
                        }

                        periodCarry.Add(new ResultCarryOver(documentId, sharedMethodologies, sharedInstances));
                    }

                    var profile = await orchestrator
                        .RunAsync(
                            run.Id,
                            documentId,
                            new PeriodKey(period),
                            bindingsByPeriod.TryGetValue(period, out var periodBindings)
                                ? periodBindings
                                : [],
                            new PhaseProgress(
                                progress, periodFloor, periodCeiling, "jobs.phaseMethodologies"),

                            // RT-23a: довідники — станом на момент прогону (AC-7).
                            run.RegistryAsOfUtc,
                            ct)
                        .ConfigureAwait(false);

                    if (profile.ForeignOwnerReads.Count > 0)
                    {
                        foreignOwnerReads.Add((documentId, period, [.. profile.ForeignOwnerReads]));
                    }

                    if (bindingPlan.PartialTables.TryGetValue(period, out var partialTables))
                    {
                        profile.ExcludeTables(partialTables);
                    }

                    runProfile.Merge(profile);
                    unmatchedRows.AddRange(profile.UnmatchedRows);
                    rejectedRows.AddRange(profile.RejectedRows);
                    missingInputRows.AddRange(profile.MissingInputRows);
                    skippedUnpublished.UnionWith(profile.SkippedUnpublishedMethodologies);
                }
            }

            // ⚠ Рік, у якому немає жодного періоду в скоупі (жодного
            // екземпляра таблиць або всі періоди закриті), лишає по собі один
            // прогін без періоду — як і раніше: задача за розкладом мусить
            // лишати слід (профіль пишеться завжди, J-1), а результатів у
            // такого прогону немає, тож «періоду 0» він не наповнить.
            // ✎ PSS-04: крок ланцюжка без прогону (якір B24 — лише формули; решту періодів закрили між кроками) прогону «без
            // періоду» не лишає — лише ланцюжок, у якому не було жодного періоду взагалі.
            // ✎ PERF-01: і ланцюжок, план якого пропустив усі місяці як незмінні, — без прогону «без періоду».
            if (periodRuns.Count == 0
                && !(chain && (stepPeriod is not null || (request.ChainDone?.Length ?? 0) > 0 || chainDecision is { Skipped.Count: > 0 })))
            {
                yearRun = NewRun(periodKey: null);
                db.CalculationRuns.Add(yearRun);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await runs.CompleteAsync(yearRun.Id, new ModuleProfile(), ct, purgeLater: supersededRuns).ConfigureAwait(false);
            }

            // Завершення — прикладний сценарій: профіль і перемикання
            // актуальності однією транзакцією (ФВ-9.11) — на кожен період.
            //
            // ⛔ D2-03: стан аркушів і періоду ПЕРЕЧИТУЄТЬСЯ в транзакції перемикання. Рішення
            // «що рахувати» (`ApprovedSheetsAsync`, `RefusedPeriodsAsync`) ухвалено на старті
            // документа, а методологічна фаза триває хвилини: подання аркуша чи закриття
            // періоду в цьому вікні інакше мовчки підмінило б числа поданого/затвердженого
            // аркуша (живе посилання, `SubmitSheetHandler`) або записало б у закритий період.
            //
            // ⛔ Y1-02: «аркуш подається» (`EnterEditNoWaitAsync` на 2-му й далі аркуші, X6-02) минає
            // за секунди. Повторюється лише КОРОТКА транзакція перемикання — методології вже
            // пораховано, і ретрай усієї задачі перерахував би їх заново саме в пік дедлайну. Відмова
            // відкочує перемикання цілком (перевірка — перша в транзакції), тож повтор безпечний.
            // Вичерпано — задача відкладається (нижче, `catch`), а не падає.
            foreach (var (period, (run, profile)) in periodRuns)
            {
                var guards = completionGuards.GetValueOrDefault(period) ?? [];
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        // ✎ Позначка після подання: зміни результатів — у набір прогону ЛИШЕ після коміту перемикання. Спроба,
                        // яку відкочено (повтор транзакції, скасування посеред неї), набору не чіпає: інакше аварійний запис
                        // позначок (FlushSubmittedMarksAsync) позначив би аркуш, чиї числа так і не змінилися.
                        var periodResultChanges = new SubmittedSheetChanges();
                        await runs
                            .CompleteAsync(
                                run.Id, profile, ct, carryOvers.GetValueOrDefault(period),
                                async token =>
                                {
                                    await GuardCompletionAsync(projectId, period, guards, request.IncludeApproved, token).ConfigureAwait(false);

                                    // ✎ B79: після воріт (стан аркушів уже під блокуванням) і ДО переносу й перемикання —
                                    // поки є з чим порівнювати.
                                    var attemptChanges = new SubmittedSheetChanges();
                                    await ResultChangesAsync(run.Id, period, guards, attemptChanges, token).ConfigureAwait(false);
                                    periodResultChanges = attemptChanges;
                                },
                                purgeLater: supersededRuns)
                            .ConfigureAwait(false);
                        submittedChanges.UnionWith(periodResultChanges);
                        break;
                    }
                    catch (ConcurrencyConflictException busy)
                        when (attempt < CompletionBusyAttempts && FormulaRecalculationJob.IsSheetBeingSubmitted(busy))
                    {
                        await Task.Delay(CompletionBusyDelay, ct).ConfigureAwait(false);
                    }
                }
            }

            // ✎ PERF-01 (D-564a, огляд H2): межа журналу комірок у мить завершення — ще під локом документа. Кожен запис комірки
            // документа ПІСЛЯ цього прогону (каскад формул з іншого місяця бере той самий лок) отримає більший Id: планувальник
            // кнопки року порівнює Id, а не годинники Api і воркера. Збій тут лишає NULL — місяць буде порахований (безпечно).
            if (request.DocumentId > 0)
            {
                foreach (var (_, (run, _)) in periodRuns)
                {
                    await db.Database
                        .ExecuteSqlAsync(
                            $"UPDATE calc.CalculationRun SET CellChangeWatermark = CAST(IDENT_CURRENT('aud.CellChange') AS bigint) WHERE Id = {run.Id}",
                            ct)
                        .ConfigureAwait(false);
                }
            }

            // ✎ Огляд M1 (PSS-06): період, який цей прогін СПРАВДІ порахував (і він уже актуальний), — запарковані на нього
            // закриваються тут і лише тут. Решту запаркованих поверне в чергу кінець задачі: свого результату вони не
            // отримали, тож хай рахують і звітують самі.
            if ((chain ? stepPeriod : request.DocumentId > 0 ? request.PeriodKey : null) is { } computed)
            {
                await SettleParkedAsync(request.DocumentId, computed, ct).ConfigureAwait(false);
            }

            // ✎ LAND-YEAR L2b: читач (скрипт, колонка Calculated чужого місяця) порахований від результатів періоду-власника,
            // який цей же прогін перераховував: «актуальними» результати стають лише на перемиканні вище, тож читач бачив
            // ПОПЕРЕДНІ числа власника (як і формули з [Period:#k], WW-03). Другий прохід — coalesced-задача періоду-читача
            // після перемикання; вона читає вже нові числа й сама нових не ставить (ForeignOwnerPass).
            // ✎ PSS-04 / огляд M3: у ланцюжку періоди стають актуальними по одному, тож читач читає вже нові числа РАНІШИХ
            // власників. Читач ПІЗНІШОГО власника ланцюжка (ще не перерахованого) й читачі поза рештою ланцюжка (MS) отримують
            // прохід ОДРАЗУ, на цьому кроці: прохід стоїть у FIFO за ланцюжком (крок зберігає своє місце), тож іде після
            // нього, а скасований чи провалений посеред року ланцюжок не лишає вже актуальних місяців без проходу.
            if (chain && jobs is not null && !request.ForeignOwnerPass)
            {
                foreach (var reader in foreignOwnerReads
                             .Where(r => r.Owners.Any(remaining.Contains))
                             .Select(r => r.Period)
                             .Distinct()
                             .Order())
                {
                    await EnqueueReaderPassAsync(request, request.DocumentId, reader, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await EnqueueForeignOwnerReadersAsync(request, scopeByDocument, foreignOwnerReads, ct).ConfigureAwait(false);
            }

            // ✎ MS: прогін лише періоду-власника (кнопка «Перерахувати» місяця) не рахував читачів інших періодів — вони
            // лишилися б зі старими числами власника. Читачів визначають матеріалізовані дані (calc.MethodologyReadTable,
            // прив'язки, роутер місяців), нової таблиці-індексу немає. У ланцюжку — без читачів із РЕШТИ ланцюжка: їх
            // порахують пізніші кроки вже з новими числами власника.
            await EnqueueOwnerPeriodReadersAsync(request, scopeByDocument, ct, chain ? remaining : null).ConfigureAwait(false);

            // ✎ UAT-recalc-ux: журнал аркушів — пропущені затверджені й перераховані подані (журнал змін + повідомлення
            // задачі). Після перемикання: записується лише те, що справді стало актуальним.
            // ✎ B79: поданий аркуш — лише якщо прогін змінив у ньому хоч одне число (`submittedChanges`).
            var sheetLog = await SheetLogAsync(request, scopeByDocument, submittedChanges, ct, blockedReaders).ConfigureAwait(false);
            submittedMarksWritten = true;

            // L-4: «No matching rule … row N». Не помилка й не відмова - решта рядків уже
            // пораховано; це слід у повідомленні задачі, щоб рядок без правила не зник мовчки.
            if (unmatchedRows.Count > 0)
            {
                await progress
                    .ReportAsync(100, JobProgressMessageCodec.Encode(NoMatchingRuleEnvelope(unmatchedRows)), ct)
                    .ConfigureAwait(false);
            }

            // L2-6: правило категорії не дало ключа окремим рядкам - відхилено лише їх, документ
            // порахований; номери рядків (без значень і назв колонок) у повідомленні задачі.
            if (rejectedRows.Count > 0)
            {
                await progress
                    .ReportAsync(100, JobProgressMessageCodec.Encode(CategoryRuleRejectedEnvelope(rejectedRows)), ct)
                    .ConfigureAwait(false);
            }

            // AN-180: рядки без значення через відсутні входи формул (проблема MissingInputs) - номери рядків у
            // повідомленні задачі, щоб тихого нуля не було й тут; колонки до заповнення - у банері над сіткою.
            if (missingInputRows.Count > 0)
            {
                await progress
                    .ReportAsync(100, JobProgressMessageCodec.Encode(MissingInputsEnvelope(missingInputRows)), ct)
                    .ConfigureAwait(false);
            }

            // G14: привʼязана методологія без опублікованої версії - попередження, а не відмова.
            if (skippedUnpublished.Count > 0)
            {
                await progress
                    .ReportAsync(100, JobProgressMessageCodec.Encode(UnpublishedMethodologyEnvelope(skippedUnpublished)), ct)
                    .ConfigureAwait(false);
            }

            // ✎ UAT-recalc-ux: підсумок журналу аркушів — ОСТАННІМ повідомленням задачі (сторінка задач показує його);
            // поіменний перелік — у журналі змін (`SheetRecalc`).
            JobProgressMessageEnvelope? sheetLogEnvelope = sheetLog.Skipped + sheetLog.RecalculatedSubmitted > 0
                ? new JobProgressMessageEnvelope(
                    "jobs.recalcSheetLog",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["skipped"] = sheetLog.Skipped.ToString(CultureInfo.InvariantCulture),
                        ["recalculatedSubmitted"] = sheetLog.RecalculatedSubmitted.ToString(CultureInfo.InvariantCulture),
                    })
                : null;

            // ✎ PERF-01 (D-564a): останній крок ланцюжка, план якого пропустив незмінні місяці, — підсумок «пропущено незмінні»
            // (з підсумком журналу аркушів усередині): людина бачить, ЩО не перераховано і чому.
            if (chain && remaining.Count == 0 && chainDecision is { Skipped.Count: > 0 } decided)
            {
                if (logger is not null && logger.IsEnabled(LogLevel.Information))
                {
                    var skippedText = string.Join(',', decided.Skipped);
                    LogSkippedUnchanged(logger, request.DocumentId, skippedText);
                }

                await progress
                    .ReportAsync(100, JobProgressMessageCodec.Encode(SkippedUnchangedEnvelope(decided.Skipped, sheetLogEnvelope)), ct)
                    .ConfigureAwait(false);
            }
            else if (sheetLogEnvelope is not null)
            {
                await progress.ReportAsync(100, JobProgressMessageCodec.Encode(sheetLogEnvelope), ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // ⛔ L1b / D4 (стенд Land 21.11): скасування (POST /jobs/{id}/cancel, межа тривалості, зупинка служби) — за
            // станом ТОКЕНА, а не типом винятку (драйвер посеред запиту кидає SqlException «Operation cancelled by user»,
            // див. JobWorker L2-01). Доти прогони кроку лишалися `Running` до AbandonedWorkSweeper (2 год): гілка нижче
            // OCE не бачила, а SqlException вела в маркування токеном, що вже скасований. Закриваємо тут, без токена.
            await CloseCancelledRunsAsync(periodRuns.Values.Select(entry => entry.Run).Append(yearRun)).ConfigureAwait(false);
            if (!submittedMarksWritten)
            {
                await FlushSubmittedMarksAsync(request, submittedChanges).ConfigureAwait(false);
            }

            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ⛔ Трекер очищається ПЕРЕД повторним записом. `EcrDbContext` тут
            // — ТОЙ САМИЙ скоуп-інстанс, яким `QuartzJobAdapter` пише
            // `itg.JobProgress` (`JobProgressStore`, той самий DI-скоуп): якщо
            // лишити в трекері сутність, чий `INSERT` щойно провалився,
            // НАСТУПНЕ `SaveChangesAsync` — навіть чуже, запис прогресу в
            // `itg.JobProgress` — повторно спробує вставити той самий
            // зіпсований рядок і провалиться теж. Тоді `QuartzJobAdapter`
            // не зможе позначити задачу `Failed`, і вона лишиться `Running`
            // назавжди — це і є справжня причина «задача висить 0 %»
            // (директива №09 §1.3), а не сам факт «catch не викликається».
            db.ChangeTracker.Clear();

            // Прогін позначається `Failed` лише якщо його `INSERT` УСПІШНО
            // відбувся (`run.Id` призначений базою): позначати нема чого,
            // якщо самого рядка в базі немає.
            //
            // ⚠ Прогонів тут може бути кілька (по одному на період): позначаються
            // вставлені й НЕ завершені. Уже завершений прогін періоду став
            // актуальним і зняв актуальність із попереднього — позначити його
            // `Failed` означало б лишити той період без актуальних результатів.
            var started = periodRuns.Values
                .Select(entry => entry.Run)
                .Append(yearRun)
                .OfType<Domain.Entities.Calculations.CalculationRun>()
                .Where(run => run.Id > 0 && run.Status == "Running")
                .ToList();

            if (started.Count > 0)
            {
                // ⛔ SEC (TIER2): `calc.CalculationRun.ErrorMessage` читає клієнт (результати,
                // журнал прогонів). Сирий `ex.Message` довільного винятку (БД, файл, мережа)
                // туди не йде: власний виняток продукту — його текст, решта — код каталогу +
                // кореляція; повний виняток лишається в журналі сервера (його кидає далі
                // `throw;` нижче, плюс запис тут із тією ж кореляцією).
                var correlationId = SafeErrorText.NewCorrelationId();
                if (!SafeErrorText.IsOwn(ex))
                {
                    LogRecalculationFailed(logger ?? NullLogger<RecalculationJob>.Instance, correlationId, ex);
                }

                var errorText = SafeErrorText.For(ex, correlationId, "the recalculation");
                foreach (var run in started)
                {
                    db.CalculationRuns.Attach(run);
                    run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: errorText);
                }

                // ⛔ Маркування `Failed` — другорядне: якщо й воно впало (зупинка бази стоїть і тут: той самий
                // `-2`/1205), початковий виняток НЕ губиться і НЕ підміняється — інакше новий `SqlException` пішов би
                // повз `SqlStallClassifier` нижче просто в `JobRetryPolicy` (30/60/120 с), і відкладення
                // (пріоритет) не відбулось би. Прогони лишаються `Running`: повтор/відкладення створить нові, а
                // завислі закриє наступне очищення прогонів.
                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (Exception markFailure) when (markFailure is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    LogMarkFailedFailed(
                        logger ?? NullLogger<RecalculationJob>.Instance, request.DocumentId, started.Count, markFailure);
                }
            }

            // ✎ Позначка після подання: закомічені зміни поданих аркушів — і при відкладенні, і при збої (повтор задачі
            // змін уже не побачить).
            if (!submittedMarksWritten)
            {
                await FlushSubmittedMarksAsync(request, submittedChanges).ConfigureAwait(false);
            }

            // ⛔ Y1-02: «аркуш подається» (фаза формул — `RecalculationService.EnterSheetsAsync`, або
            // вичерпані повтори перемикання вище) — не провал і не ретрай: `JobRetryPolicy` дає лише
            // 30/60/120 с, що вичерпалось би за годину дедлайну, і прогін лишився б `Failed`, а
            // методології — застарілими (`staleMethodologyResults` блокує наступне «Подати»). Як і
            // `FormulaRecalculationJob`: задача повертається в чергу, не рахуючи спроби (межа —
            // `JobDeferral.MaxDeferral`). Незавершені прогони вже позначено вище — повтор створить нові.
            if (ex is ConcurrencyConflictException busy && FormulaRecalculationJob.IsSheetBeingSubmitted(busy))
            {
                throw new JobDeferredException(RecalculationDocumentLock.DeferDelay, busy.Message);
            }

            // ⛔ F1 (WW-02): `ECR-DOC-4091` «lockTimeout» (SqlException 1222 після `SET LOCK_TIMEOUT` запису комірок)
            // — стан зайнятості, а не вердикт про дані: чужа довга операція чи сусідні прогони, що ескалювали
            // блокування, мине. Раніше він ішов у `JobRetryPolicy` (3 ретраї, 30+60+120 с) і задача падала
            // `Failed` із застарілими числами в документі (70 із 91 після переносу версії). Тепер — відкладення
            // без спроби ретраю, потік і слот вільні; стеля — `JobDeferral.MaxDeferral`, після неї `Failed` із
            // конвертом «ресурс зайнятий» (причина видна), а кнопка «Перерахувати» проєкту лишається повтором.
            if (RecalculationDocumentLock.IsLockWaitTimeout(ex))
            {
                throw new JobDeferredException(
                    RecalculationDocumentLock.LockWaitDeferDelay(request.DocumentId),
                    "Document data is busy with a long operation; recalculation deferred.",
                    RecalculationDocumentLock.Resource(request.DocumentId));
            }

            // ⛔ Зупинка бази (15.10, EcrLandUi): автоприріст файлу `Ecr_hot` стояв 71 с, `SqlException -2`
            // (CommandTimeout) вистрілив у трьох прогонах одночасно, і три задачі пішли в `JobRetryPolicy`
            // (30/60/120 с) — по собі лишились `Failed`-прогони. Таймаут команди й жертва дедлока (1205) — як і
            // 1222 вище, стан бази, що минає, а не вердикт про дані: відкладення без спроби ретраю, слот і
            // applock вільні. Стеля та сама — `JobDeferral.MaxDeferral` (30 хв від першого відкладення); після
            // неї задача `Failed` з конвертом «ресурс зайнятий» (нескінченного відкладення немає), а систематичний
            // таймаут стає видимим. Незавершені прогони вже позначено `Failed` вище, повтор створить нові.
            if (SqlStallClassifier.Find(ex) is { } stall)
            {
                var stallDelay = RecalculationDocumentLock.LockWaitDeferDelay(request.DocumentId);

                // Оператор бачить SQL-таймаут (-2/1205) у журналі, а не лише «ресурс зайнятий» після стелі.
                LogSqlStallDeferred(
                    logger ?? NullLogger<RecalculationJob>.Instance, request.DocumentId, stall.Number, stallDelay);

                throw new JobDeferredException(
                    stallDelay,
                    "Database stalled (command timeout or deadlock victim); recalculation deferred.",
                    RecalculationDocumentLock.Resource(request.DocumentId));
            }

            // ✎ PERF-06 (D-566, огляд F1): крок ланцюжка каскаду впав — решта плану (з дописаними злиттям місяцями й пізнішими
            // читачами) їде з винятком; якщо провал остаточний (без ретраю), виконавець ставить її новою задачею каскаду на ту
            // саму ціль, а рядок цієї — Failed з місяцем провалу (JobFailureFollowUp). Повтор кроку (транзієнтний збій) іде з
            // payload як є. Сам місяць провалу — у новій задачі немає: його прогін Failed (видно в журналі прогонів), а
            // детерміновану помилку повтор не виправить.
            if (chain && request.CascadePeriods is not null && stepPeriod is { } failedStep)
            {
                int[] rest = [.. remaining.Concat(request.CascadeAppend ?? []).Where(p => p != failedStep).Distinct().Order()];
                if (rest.Length > 0)
                {
                    JobFailureFollowUp.Attach(
                        ex, JsonSerializer.Serialize(CascadeFollowUpRequest(request, rest), PayloadOptions), failedStep, rest);
                }
            }

            throw;
        }

        // ПРД-13 (НФ-8.6.4): вимір ПІСЛЯ завершення — лише читає годинник, логіки перерахунку не торкається.
        // Провалена задача сюди не доходить: тривалість до відмови не є тривалістю перерахунку.
        // ✎ PSS-04 / огляд M2: ланцюжок — один вимір після ОСТАННЬОГО кроку: сума часу РОБОТИ кроків (ChainElapsedMs),
        // без очікувань у черзі між ними — інакше річний розклад давав би хибне «понад бюджет» і Degraded на /health/ready.
        var chainContinues = chain && remaining.Count > 0;
        var worked = TimeSpan.FromMilliseconds(request.ChainElapsedMs ?? 0) + (clock.UtcNow - startedAt);
        if (budget is not null && !chainContinues)
        {
            await budget
                .ObserveAsync(projectId, request.DocumentId, fullYear: request.PeriodKey is null, worked, progress, ct)
                .ConfigureAwait(false);
        }

        // B179 L2: після УСПІШНОГО прогону — документи й періоди, які він рахував (провалений сюди не доходить); ланка —
        // з постановки залежного (ланцюжок документів-джерел).
        // ✎ PSS-04: у ланцюжку — після КОЖНОГО кроку: період уже актуальний, залежні від нього не чекають решти року.
        if (completionHook is not null && scopeByDocument.Count > 0)
        {
            await completionHook.OnSucceededAsync(scopeByDocument, request.SourceHop, ct).ConfigureAwait(false);
        }

        if (chainContinues)
        {
            int[] chainDone = [.. ChainScope(request, scopeByDocument).GetValueOrDefault(request.DocumentId) ?? []];
            var next = request with
            {
                ChainAfter = stepPeriod,
                ChainDone = chainDone,
                ChainElapsedMs = (long)Math.Max(0, worked.TotalMilliseconds),

                // ✎ L1b / D3: шкала наступного кроку — з його першого звіту: зроблені (з цим кроком) + решта скоупу.
                // ✎ PERF-02: місяць, дописаний на повтор, рахується ще раз (крок, а не місяць) — N лише росте.
                ChainTotal = chainDone.Length + remaining.Count,

                // ✎ PERF-01/02: решта плану, пропущені незмінні, перевірку злиття виконано.
                ChainPlan = [.. remaining],
                ChainSkipped = chainDecision is { Skipped.Count: > 0 } skippedSoFar ? [.. skippedSoFar.Skipped] : null,
                ChainRecheck = false,

                // ✎ PERF-06 (D-566): злиті періоди каскаду й затравки вже в плані (решті) — наступний крок їх не дописує вдруге.
                CascadeAppend = null,
                CascadeSeeds = null,
            };

            // ✎ PSS-06: задачі документа, що чекають на ПІЗНІШІ періоди ланцюжка, — його ж робота: паркуються до кроку
            // свого періоду (закриються тоді ж), а не рахуються окремо перед ним, щоб ланцюжок порахував їх удруге.
            await ParkRemainingAsync(next, remaining, ct).ConfigureAwait(false);

            // ✎ Огляд H2: без відкладення — крок зберігає місце ланцюжка в FIFO; правці комірки документа, що чекає, claim
            // лейна перерахунку поступається сам (DbJobQueue.EditFirstExclusion).
            throw new JobContinuedException(JsonSerializer.Serialize(next, PayloadOptions), TimeSpan.Zero, stepPeriod!.Value);
        }
    }

    /// <summary>
    /// ✎ PSS-04: періоди, які ланцюжок уже зробив (зі зробленими цим кроком), — у формі <c>scopeByDocument</c>.
    /// </summary>
    private static Dictionary<long, int[]> ChainScope(RecalculationRequest request, Dictionary<long, int[]> scopeByDocument)
    {
        // ✎ PERF-02 (D-564b): зроблений місяць, дописаний злиттям на повтор, лишається в переліку двічі — це лічильник КРОКІВ
        // шкали «період k з N» (k не стрибає назад); множиною «зроблені» переліку користуються через Distinct.
        var done = (request.ChainDone ?? []).Concat(scopeByDocument.GetValueOrDefault(request.DocumentId) ?? []).Order().ToArray();
        return done.Length == 0 ? [] : new Dictionary<long, int[]> { [request.DocumentId] = done };
    }

    /// <summary>
    /// ✎ PSS-06 (D-561): чи прогін <paramref name="coverer"/> за період <paramref name="period"/> робить роботу задачі
    /// <paramref name="candidate"/> з надлишком — тоді та, що чекає, паркується (<see cref="IJobQueue.ParkCoveredAsync"/>).
    /// </summary>
    /// <remarks>
    /// Покрита — задача ОДНОГО періоду того самого документа, обсяг якої не ширший: аркуш (ціла задача покриває будь-який
    /// аркуш, аркуш — лише той самий), затверджені (перекриття адміністратора покриває звичайну, не навпаки), прохід
    /// читача (звичайний прогін покриває прохід, прохід — лише прохід: він не ставить нових проходів), ланка джерел
    /// (покривна з меншою чи тією самою ланкою — її хук не впреться в стелю раніше). Задачі «за рік»/ланцюжки не
    /// покриваються: їхній обсяг ширший за один період.
    /// </remarks>
    internal static bool Covers(RecalculationRequest coverer, int period, RecalculationRequest candidate)
    {
        ArgumentNullException.ThrowIfNull(coverer);
        ArgumentNullException.ThrowIfNull(candidate);

        return candidate.DocumentId == coverer.DocumentId
               && candidate.DocumentId > 0
               && candidate.PeriodKey == period
               && candidate.ChainAfter is null
               && (coverer.SheetDefId is null || coverer.SheetDefId == candidate.SheetDefId)
               && (coverer.IncludeApproved || !candidate.IncludeApproved)
               && (!coverer.ForeignOwnerPass || candidate.ForeignOwnerPass)
               && coverer.SourceHop <= candidate.SourceHop
               && SameAttribution(coverer.TriggeredByUserId, candidate.TriggeredByUserId)
               && SameAttribution(coverer.OverrideUserId, candidate.OverrideUserId);
    }

    /// <summary>
    /// ✎ Огляд L6: чия це робота. Задачу, яку поставила людина (чи перекриття адміністратора), покриває лише прогін ТІЄЇ
    /// САМОЇ людини: журнал аркушів («хто перерахував», «чиє перекриття й причина») і задача, яку людина опитує, мусять
    /// лишатись правдивими. Системна задача (без автора) покривається будь-яким прогоном.
    /// </summary>
    private static bool SameAttribution(int? coverer, int? candidate) => candidate is null || candidate == coverer;

    /// <summary>
    /// ✎ PSS-06: паркує задачі документа, що чекають на <paramref name="period"/> і чию роботу робить цей прогін.
    /// </summary>
    /// <returns>Скільки запарковано (0 — черга не вміє, оренди немає, покритих немає).</returns>
    /// <remarks>
    /// ⚠ Оптимізація, а не умова коректності: збій — у журнал, прогін іде далі (щонайбільше подвійна робота).
    /// </remarks>
    private async Task<int> ParkCoveredAsync(RecalculationRequest request, int period, CancellationToken ct)
        => (await ParkAsync(request, [period], ct).ConfigureAwait(false)).Count;

    /// <summary>✎ PSS-06: паркує задачі документа на ПІЗНІШІ періоди ланцюжка (до їхнього кроку).</summary>
    /// <remarks>
    /// ⛔ Огляд H2 (§6 п. 2, ФВ-12.2): ручний перерахунок і автоперерахунок (<c>-auto</c>) на пізніший місяць НЕ паркуються —
    /// вони не чекають годинами, поки ланцюжок дійде до їхнього місяця; їх покриває лише крок їхнього ж періоду, якщо вони
    /// ще чекають на його старті.
    /// </remarks>
    private Task<IReadOnlyList<string>> ParkRemainingAsync(
        RecalculationRequest next, IReadOnlyList<int> remaining, CancellationToken ct)
        => ParkAsync(next, remaining, ct, laterPeriods: true);

    private async Task<IReadOnlyList<string>> ParkAsync(
        RecalculationRequest coverer, IReadOnlyList<int> periods, CancellationToken ct, bool laterPeriods = false)
    {
        if (queue is not { SupportsContinuation: true } || lease?.Current is not { } claim || coverer.DocumentId <= 0
            || periods.Count == 0)
        {
            return [];
        }

        try
        {
            var covered = new List<CoveredJob>();
            foreach (var row in await queue.ListQueuedForDocumentAsync(coverer.DocumentId, ct).ConfigureAwait(false))
            {
                if (!string.Equals(row.Lane, JobLanes.Recalc, StringComparison.Ordinal)
                    || !string.Equals(row.JobCode, JobListKinds.RecalculationJobCode, StringComparison.Ordinal)
                    || string.Equals(row.JobId, claim.JobId, StringComparison.Ordinal)
                    || TryParse(row.PayloadJson) is not { PeriodKey: { } candidatePeriod } candidate
                    || !periods.Contains(candidatePeriod)
                    || !Covers(coverer, candidatePeriod, candidate)
                    || (laterPeriods && (candidate.TriggeredByUserId is not null
                                         || row.TargetKey?.EndsWith(AutoTargetSuffix, StringComparison.Ordinal) == true)))
                {
                    continue;
                }

                covered.Add(new CoveredJob(row.JobId, candidatePeriod));
            }

            if (covered.Count == 0)
            {
                return [];
            }

            var parked = await queue.ParkCoveredAsync(claim, covered, ct).ConfigureAwait(false);
            if (parked.Count > 0 && logger is not null && logger.IsEnabled(LogLevel.Information))
            {
                var shownPeriods = string.Join(", ", periods);
                var shownJobs = string.Join(", ", parked);
                LogParked(logger, coverer.DocumentId, shownPeriods, shownJobs);
            }

            return parked;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogParkFailed(logger ?? NullLogger<RecalculationJob>.Instance, coverer.DocumentId, ex);
            return [];
        }
    }

    /// <summary>Закінчення цілі автоперерахунку (<see cref="CalculationTrigger.AutoTargetOf"/>).</summary>
    private const string AutoTargetSuffix = "-auto";

    /// <summary>✎ Огляд M1: закриває запарковані на порахований <paramref name="period"/> (збій — у журнал: їх поверне кінець задачі).</summary>
    private async Task SettleParkedAsync(long documentId, int period, CancellationToken ct)
    {
        if (queue is not { SupportsContinuation: true } || lease?.Current is not { } claim)
        {
            return;
        }

        try
        {
            await queue.SettleParkedAsync(claim, period, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogParkFailed(logger ?? NullLogger<RecalculationJob>.Instance, documentId, ex);
        }
    }

    private static RecalculationRequest? TryParse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "RecalculationJob: документ {DocumentId}, періоди {Periods}: запарковано задачі {JobIds} — їхню роботу робить "
                  + "цей прогін (PSS-06); закриються разом із ним.")]
    private static partial void LogParked(ILogger logger, long documentId, string periods, string jobIds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: документ {DocumentId}: не вдалося запаркувати покриті задачі чи прочитати чергу документа; "
                  + "перерахунок іде далі (щонайбільше подвійна робота).")]
    private static partial void LogParkFailed(ILogger logger, long documentId, Exception exception);

    /// <summary>
    /// ✎ Перф-аудит 20.11 (B80 N5): задача «з затвердженими» після взяття лока документа скасовує звичайну задачу
    /// залежних того самого документа й періоду, що ЧЕКАЄ (конверт «покрито задачею {jobId}»).
    /// </summary>
    /// <remarks>
    /// ⛔ Безпечно саме тут: тригер ставить звичайну ПІСЛЯ коміту зміни, що її викликала, а ця задача ще не прочитала
    /// жодного входу — отже прочитає й ту зміну. Виконувану звичайну не чіпаємо (лок у нас — виконуваної немає).
    /// <para>
    /// ⚠ Оптимізація, а не умова коректності: збій скасування — у журнал, перерахунок іде далі (звичайна виконається
    /// окремо, щонайбільше подвійна робота). Якщо ж ця задача сама провалиться, скасовану поверне
    /// <see cref="ApprovedDependentsFallback"/>.
    /// </para>
    /// </remarks>
    private async Task CancelCoveredDependentsAsync(RecalculationRequest request, CancellationToken ct)
    {
        if (!request.IncludeApproved || jobs is null || request.DocumentId <= 0 || request.PeriodKey is not { } period)
        {
            return;
        }

        try
        {
            await jobs
                .CancelQueuedAsync<IRecalculationJob>(
                    CalculationTrigger.DependentsTargetOf(request.DocumentId, new PeriodKey(period)), lease?.Current?.JobId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCancelCoveredFailed(logger ?? NullLogger<RecalculationJob>.Instance, request.DocumentId, period, ex);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: документ {DocumentId}, період {PeriodKey}: не вдалося скасувати звичайну задачу залежних, "
                  + "покриту задачею «з затвердженими»; перерахунок іде далі (щонайбільше подвійна робота).")]
    private static partial void LogCancelCoveredFailed(ILogger logger, long documentId, int periodKey, Exception exception);

    /// <summary>
    /// ✎ LAND-YEAR L2b: ставить другий прохід для періодів-читачів, що читали результати періодів-власників, перерахованих
    /// у ТОМУ САМОМУ прогоні (їхні нові числа стали актуальними лише після перемикання).
    /// </summary>
    /// <param name="request">Завдання; <see cref="RecalculationRequest.ForeignOwnerPass"/> — другий прохід нових не ставить.</param>
    /// <param name="scopeByDocument">Періоди, які прогін рахував, за документами.</param>
    /// <param name="reads">Читачі: документ, період, періоди-власники.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task EnqueueForeignOwnerReadersAsync(
        RecalculationRequest request,
        Dictionary<long, int[]> scopeByDocument,
        List<(long DocumentId, int Period, IReadOnlyCollection<int> Owners)> reads,
        CancellationToken ct)
    {
        if (jobs is null || request.ForeignOwnerPass || reads.Count == 0)
        {
            return;
        }

        foreach (var (documentId, period, owners) in reads)
        {
            if (!scopeByDocument.TryGetValue(documentId, out var scope)
                || !owners.Any(owner => owner != period && scope.Contains(owner)))
            {
                continue;
            }

            await EnqueueReaderPassAsync(request, documentId, period, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Ставить coalesced-прохід періоду-читача; сам прохід нових не ставить (<c>ForeignOwnerPass</c>).</summary>
    /// <remarks>
    /// ⚠ PSS-07 (D-561): прохід читача — без відкладення каскаду. Ланцюжок (PSS-04) ставить проходи ОДИН раз, після
    /// останнього кроку; прохід за названий період — один на період-читача й злитий за ціллю.
    /// </remarks>
    private async Task EnqueueReaderPassAsync(RecalculationRequest request, long documentId, int period, CancellationToken ct)
        => _ = await jobs!
            .EnqueueCoalescedAsync<IRecalculationJob>(
                Ecr.Application.Documents.RecalculateDocumentHandler.TargetOf(documentId, new PeriodKey(period)),
                ReaderPassRequest(request, documentId, period),
                ct,
                request.TriggeredByUserId)
            .ConfigureAwait(false);

    private static RecalculationRequest ReaderPassRequest(RecalculationRequest request, long documentId, int period)
        => request with
        {
            DocumentId = documentId,
            PeriodKey = period,
            Year = null,
            SheetDefId = null,
            FanOutParentJobId = null,
            ForeignOwnerPass = true,
            ChainAfter = null,
            ChainDone = null,
            ChainElapsedMs = null,
            ChainTotal = null,
            SkipUnchanged = false,
            ForceFull = false,
            ScreenPeriods = null,
            ChainPlan = null,
            ChainSkipped = null,
            ChainRecheck = false,
            CascadePeriods = null,
            CascadeAppend = null,
            CascadeSeeds = null,
        };

    /// <summary>
    /// ✎ PERF-06 (огляд F1): нова задача каскаду (не ланцюжок) на періоди <paramref name="periods"/> — решта роботи ланцюжка,
    /// крок якого остаточно впав.
    /// </summary>
    private static RecalculationRequest CascadeFollowUpRequest(RecalculationRequest request, int[] periods)
        => request with
        {
            ChainAfter = null,
            ChainDone = null,
            ChainElapsedMs = null,
            ChainTotal = null,
            ChainPlan = null,
            ChainSkipped = null,
            ChainRecheck = false,
            CascadePeriods = periods,
            CascadeAppend = null,
            CascadeSeeds = null,
        };

    /// <summary>
    /// ✎ MS: ставить coalesced-прохід періодам-читачам, ЯКІ ПОЗА прогоном, коли прогін перерахував період-власник колонок
    /// <c>Calculated</c> цих читачів (місячні колонки версії YearWithMonthColumns, не-місячні — якір року).
    /// </summary>
    /// <param name="request">Завдання; <see cref="RecalculationRequest.ForeignOwnerPass"/> — другий прохід нових не ставить.</param>
    /// <param name="scopeByDocument">Періоди, які прогін рахував, за документами.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="alsoExclude">✎ PSS-04 / огляд M3: періоди-читачі, яких прохід не ставить (решта ланцюжка порахує їх сама).</param>
    /// <remarks>
    /// <para>
    /// Читач періоду Q — методологія-скрипт, що читає (<c>calc.MethodologyReadTable</c>) таблицю документа, у якої колонка
    /// <c>Calculated</c> для Q має власника з перерахованих періодів (<see cref="IMonthColumnRouter.ForeignColumnsAsync"/>),
    /// і яка прив'язана до екземпляра таблиці в Q. Документ не місячного проєкту й версії без розкладки року не платять
    /// нічого, окрім одного читання роутера.
    /// </para>
    /// <para>
    /// ✎ F7 (огляд PERF-06): і колонки <c>Formula</c> — їх переписує прогін власника й каскад (D-566), а скрипт іншого місяця
    /// через псевдонім таблиці (<c>Src.F[m]</c>) доти лишався зі старим числом. ✎ D-568: і читач власної таблиці
    /// (<c>row.F[m]</c>) — за полями, які скрипт читає (<c>calc.MethodologyReadField</c>, записані при публікації).
    /// </para>
    /// <para>
    /// ⛔ Затверджені аркуші читача не перераховуються (D-328, D-493): прохід Q їх пропускає й лишає позначку
    /// «пропущено» (<see cref="SheetLogAsync"/>). Період, де затверджено ВСЕ (чи закритий, чи проєкт архівний), прохід не
    /// ставить — як розклад проєкту; позначку в разі «затверджено все» пишемо тут же, бо проходу, що її писав би, не буде.
    /// </para>
    /// </remarks>
    private async Task EnqueueOwnerPeriodReadersAsync(
        RecalculationRequest request, Dictionary<long, int[]> scopeByDocument, CancellationToken ct,
        IReadOnlyList<int>? alsoExclude = null)
    {
        if (jobs is null || monthRouter is not { Enabled: true } || request.ForeignOwnerPass || scopeByDocument.Count == 0)
        {
            return;
        }

        foreach (var (documentId, scope) in scopeByDocument)
        {
            if (scope.Length == 0)
            {
                continue;
            }

            var routing = await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false);
            if (routing is null || !monthRouter.RoutesComputedVersion(routing.TemplateVersionId))
            {
                continue;
            }

            foreach (var period in await ReaderPeriodsOfOwnersAsync(routing, scope, ct).ConfigureAwait(false))
            {
                // ✎ Огляд M3: читача з решти ланцюжка порахує його пізніший крок.
                if (alsoExclude is not null && alsoExclude.Contains(period))
                {
                    continue;
                }

                var child = ReaderPassRequest(request, documentId, period);
                var skip = await ReaderPassSkipAsync(child, ct).ConfigureAwait(false);
                if (skip == RecalculationWriteDenial.None)
                {
                    await EnqueueReaderPassAsync(request, documentId, period, ct).ConfigureAwait(false);
                }
                else if (skip == RecalculationWriteDenial.SheetsSubmitted)
                {
                    await SheetLogAsync(
                            child, new Dictionary<long, int[]> { [documentId] = [period] }, new SubmittedSheetChanges(), ct)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>Чи ставити прохід читача: <see cref="RecalculationWriteDenial.None"/> — так.</summary>
    private async Task<RecalculationWriteDenial> ReaderPassSkipAsync(RecalculationRequest child, CancellationToken ct)
    {
        try
        {
            return await FanOutSkipAsync(child, ct).ConfigureAwait(false);
        }
        catch (Ecr.Application.Errors.BusinessRuleException)
        {
            // Закритий період (D-468) тощо: прохід лише відмовив би; закритий місяць перераховується лише після Reopen.
            return RecalculationWriteDenial.PeriodClosed;
        }
    }

    /// <summary>
    /// ✎ D-568: що читає з ВЛАСНОЇ таблиці методологія-скрипт (усі її опубліковані/застарілі версії й бібліотеки, які вони
    /// імпортують), — з <c>calc.MethodologyReadField</c>.
    /// </summary>
    /// <param name="All">Читає будь-що: версія без запису (до D-568, дозапис не дійшов) чи скрипт, що не скомпілювався.</param>
    /// <param name="Fields">Поле → «місячне читається за інший місяць».</param>
    internal sealed record ScriptFieldReads(bool All, IReadOnlyDictionary<string, bool> Fields)
    {
        /// <summary>Чи читає скрипт місяця-читача число колонки <paramref name="column"/>, чий власник — інший період.</summary>
        /// <param name="column">Колонка з чужим власником.</param>
        /// <param name="monthAliases">Псевдоніми місяця колонки (<c>cfg.MonthInputAlias</c>), якщо є.</param>
        public bool ReadsForeign(Domain.Entities.Configuration.ColumnDef column, IReadOnlyList<string>? monthAliases)
        {
            if (All)
            {
                return true;
            }

            // Не-місячна колонка річної розкладки (власник — якір): будь-яке читання поля з іншого місяця.
            if (!column.IsMonthColumn || column.MonthNumber is not { } month)
            {
                return Fields.ContainsKey(column.Code);
            }

            // Місячна `X_Mmm` (чи псевдонім місяця): лише читання за ІНШИЙ місяць — свій місяць колонки читача не чужий.
            var suffix = "_M" + month.ToString("00", CultureInfo.InvariantCulture);
            var names = (monthAliases ?? []).ToList();
            if (column.Code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && column.Code.Length > suffix.Length)
            {
                names.Add(column.Code[..^suffix.Length]);
            }

            return names.Exists(n => Fields.TryGetValue(n, out var otherMonths) && otherMonths);
        }
    }

    /// <summary>Глибина імпортів бібліотек при зборі полів читання (як у <c>ScriptsReadMonthColumnsAsync</c>).</summary>
    private const int MaxReadFieldImportDepth = 8;

    /// <summary>
    /// ✎ D-568: поля власної таблиці, які читають методології-скрипти <paramref name="methodologyIds"/>; методології без
    /// опублікованої версії-скрипта (формульні) — не в словнику.
    /// </summary>
    internal async Task<Dictionary<int, ScriptFieldReads>> ScriptFieldReadsAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct)
    {
        var result = new Dictionary<int, ScriptFieldReads>();
        var versionsOf = new Dictionary<int, List<int>>();
        var importsOf = new Dictionary<int, List<int>>();
        var pending = methodologyIds.Distinct().ToList();
        for (var depth = 0; pending.Count > 0; depth++)
        {
            var batch = pending.Where(id => !versionsOf.ContainsKey(id)).ToList();
            pending.Clear();
            if (batch.Count == 0)
            {
                break;
            }

            var versions = await db.MethodologyVersions
                .AsNoTracking()
                .Where(v => batch.Contains(v.MethodologyId)
                            && v.Engine == Domain.Enums.MethodologyEngine.Script
                            && (v.Status == Domain.Enums.TemplateVersionStatus.Published
                                || v.Status == Domain.Enums.TemplateVersionStatus.Deprecated))
                .Select(v => new { v.Id, v.MethodologyId })
                .OrderBy(v => v.Id)
                .Take(MaxBindings)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var id in batch)
            {
                versionsOf[id] = [.. versions.Where(v => v.MethodologyId == id).Select(v => v.Id)];
            }

            if (versions.Count == 0)
            {
                continue;
            }

            var versionIds = versions.Select(v => v.Id).ToList();
            var imports = await db.MethodologyImports
                .AsNoTracking()
                .Where(i => versionIds.Contains(i.MethodologyVersionId))
                .Select(i => new { i.MethodologyVersionId, i.ImportedMethodologyId })
                .OrderBy(i => i.MethodologyVersionId)
                .ThenBy(i => i.ImportedMethodologyId)
                .Take(MaxBindings)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            foreach (var version in versions)
            {
                importsOf[version.Id] = [.. imports.Where(i => i.MethodologyVersionId == version.Id).Select(i => i.ImportedMethodologyId)];
            }

            // Імпорти глибше за стелю не розібрано — «читає будь-що» (позначка нижче через відсутній запис).
            if (depth < MaxReadFieldImportDepth)
            {
                pending.AddRange(imports.Select(i => i.ImportedMethodologyId));
            }
        }

        var allVersionIds = versionsOf.Values.SelectMany(v => v).Distinct().ToList();
        if (allVersionIds.Count == 0)
        {
            return result;
        }

        var rows = await db.MethodologyReadFields
            .AsNoTracking()
            .Where(f => allVersionIds.Contains(f.MethodologyVersionId))
            .Select(f => new { f.MethodologyVersionId, f.FieldCode, f.OtherMonths })
            .OrderBy(f => f.MethodologyVersionId)
            .ThenBy(f => f.FieldCode)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var byVersion = rows.GroupBy(r => r.MethodologyVersionId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var methodologyId in methodologyIds.Distinct())
        {
            if (versionsOf.GetValueOrDefault(methodologyId) is not { Count: > 0 })
            {
                continue;
            }

            var all = false;
            var fields = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<int>();
            var queue = new Queue<int>([methodologyId]);
            while (queue.Count > 0 && !all)
            {
                var current = queue.Dequeue();
                if (!seen.Add(current))
                {
                    continue;
                }

                // Методологія, чиї версії не зібрано (імпорт за стелею глибини), — невідомо, отже «будь-що».
                if (!versionsOf.TryGetValue(current, out var versions))
                {
                    all = true;
                    break;
                }

                foreach (var versionId in versions)
                {
                    if (!byVersion.TryGetValue(versionId, out var read))
                    {
                        all = true;
                        break;
                    }

                    foreach (var field in read.Where(r => r.FieldCode.Length > 0))
                    {
                        if (field.FieldCode == Domain.Entities.Calculations.MethodologyReadField.AllFields)
                        {
                            all = true;
                        }

                        fields[field.FieldCode] = fields.GetValueOrDefault(field.FieldCode) || field.OtherMonths;
                    }

                    foreach (var imported in importsOf.GetValueOrDefault(versionId) ?? [])
                    {
                        queue.Enqueue(imported);
                    }
                }
            }

            result[methodologyId] = new ScriptFieldReads(all, fields);
        }

        return result;
    }

    /// <summary>
    /// Колонка, число якої період-читач бере в періоду-власника і яку переписує прогін власника: <c>Calculated</c> (результат
    /// методології) і ✎ F7 <c>Formula</c> (формула шаблону, яку переписує й каскад формул).
    /// </summary>
    private static bool OwnerReadColumn(Domain.Enums.CellDataType type)
        => type is Domain.Enums.CellDataType.Calculated or Domain.Enums.CellDataType.Formula;

    /// <summary>
    /// Періоди року (поза <paramref name="scope"/>), де є читач колонки <c>Calculated</c> чи ✎ F7 <c>Formula</c> з власником у
    /// <paramref name="scope"/>.
    /// </summary>
    private async Task<IReadOnlyList<int>> ReaderPeriodsOfOwnersAsync(
        MonthRoutingDocument routing, int[] scope, CancellationToken ct)
    {
        var tables = (await db.TableDefs
                .AsNoTracking()
                .Include(t => t.Columns)
                .Where(t => !t.IsDeleted
                            && db.SheetDefs.Any(s => s.Id == t.SheetDefId && s.TemplateVersionId == routing.TemplateVersionId))
                .OrderBy(t => t.Id)
                .Take(MaxBindings)
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Where(t => monthRouter!.AppliesToTable(t) && t.Columns.Any(c => !c.IsDeleted && OwnerReadColumn(c.DataType)))
            .ToList();
        if (tables.Count == 0)
        {
            return [];
        }

        var runtime = new ScriptRuntimeStore(db);
        var readers = await runtime
            .GetReadersOfTablesAsync([.. tables.Select(t => t.Code).Distinct(StringComparer.OrdinalIgnoreCase)], ct)
            .ConfigureAwait(false);

        // ✎ MS: читач ВЛАСНОЇ таблиці (`rows from TA; … row.X[m]`) таблицею читання не позначений (вона лише для
        // псевдонімів `table Src = …`), зате публікація пише ребро «читач → джерело» (calc.MethodologyDependency) на
        // методологію, чий вихід лежить у колонці X. Тож читачі колонки — ті, хто має ребро на її методології-джерела.
        var edges = await runtime.GetDependencyEdgesAsync(ct).ConfigureAwait(false);

        var tableIds = tables.Select(t => t.Id).ToList();
        var bound = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableIds.Contains(b.TableDefId))
            .Select(b => new { b.TableDefId, b.ColumnDefId, b.MethodologyId })
            .Distinct()
            .OrderBy(b => b.TableDefId)
            .ThenBy(b => b.ColumnDefId)
            .ThenBy(b => b.MethodologyId)
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ D-568: поля власної таблиці, які читають прив'язані скрипти (calc.MethodologyReadField, записані при публікації).
        var fieldReads = bound.Count == 0
            ? new Dictionary<int, ScriptFieldReads>()
            : await ScriptFieldReadsAsync([.. bound.Select(b => b.MethodologyId).Distinct()], ct).ConfigureAwait(false);
        if (bound.Count == 0 || (readers.Count == 0 && edges.Count == 0 && fieldReads.Count == 0))
        {
            return [];
        }

        var aliasesByColumn = new Dictionary<int, List<string>>();
        if (fieldReads.Count > 0)
        {
            foreach (var alias in await db.MonthInputAliases
                         .AsNoTracking()
                         .Where(a => tableIds.Contains(a.TableDefId))
                         .Select(a => new { a.ColumnDefId, a.Alias })
                         .OrderBy(a => a.ColumnDefId)
                         .ThenBy(a => a.Alias)
                         .Take(MaxBindings)
                         .ToListAsync(ct)
                         .ConfigureAwait(false))
            {
                if (!aliasesByColumn.TryGetValue(alias.ColumnDefId, out var names))
                {
                    aliasesByColumn[alias.ColumnDefId] = names = [];
                }

                names.Add(alias.Alias);
            }
        }

        var candidates = new List<int>();
        foreach (var year in scope.Select(p => new PeriodKey(p).Year).Distinct())
        {
            candidates.AddRange(
                (await monthRouter!.YearPeriodKeysAsync(routing.ProjectId, year, ct).ConfigureAwait(false))
                .Where(key => !scope.Contains(key)));
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var boundTableDefIds = bound.Select(b => b.TableDefId).Distinct().ToList();
        var instances = await db.TableInstances
            .AsNoTracking()
            .OriginalOnly()
            .Where(i => i.DocumentId == routing.DocumentId
                        && candidates.Contains(i.PeriodKeyValue)
                        && boundTableDefIds.Contains(i.TableDefId))
            .Select(i => new { i.PeriodKeyValue, i.TableDefId })
            .Distinct()
            .OrderBy(i => i.PeriodKeyValue)
            .ThenBy(i => i.TableDefId)
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var result = new List<int>();
        foreach (var inPeriod in instances.GroupBy(i => i.PeriodKeyValue))
        {
            var tableDefIds = inPeriod.Select(i => i.TableDefId).ToHashSet();
            var periodReaders = bound.Where(b => tableDefIds.Contains(b.TableDefId)).Select(b => b.MethodologyId).ToHashSet();
            var readCodes = readers
                .Where(r => periodReaders.Contains(r.MethodologyId))
                .Select(r => r.TableCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var table in tables.Where(t => tableDefIds.Contains(t.Id)))
            {
                var owned = (await monthRouter!
                        .ForeignColumnsAsync(routing, table, new PeriodKey(inPeriod.Key), ct)
                        .ConfigureAwait(false))
                    .Where(f => OwnerReadColumn(f.Column.DataType) && scope.Contains(f.OwnerPeriod.Value))
                    .ToList();
                if (owned.Count == 0)
                {
                    continue;
                }

                var foreign = owned
                    .Where(f => f.Column.DataType == Domain.Enums.CellDataType.Calculated)
                    .Select(f => f.Column.Id)
                    .ToHashSet();

                // Читач через псевдонім таблиці (calc.MethodologyReadTable; ✎ F7 — і для колонок Formula), читач власної
                // таблиці за ребром на методологію-джерело колонки Calculated (MS) або ✎ D-568 — прив'язаний скрипт, що читає
                // саме це поле: не-місячне (власник — якір) будь-як, місячне — за ІНШИЙ місяць (calc.MethodologyReadField).
                var sources = bound
                    .Where(b => b.TableDefId == table.Id && foreign.Contains(b.ColumnDefId))
                    .Select(b => b.MethodologyId)
                    .ToHashSet();
                var ownReader = bound
                    .Where(b => b.TableDefId == table.Id)
                    .Any(b => edges.Any(e => e.FromMethodologyId == b.MethodologyId && sources.Contains(e.ToMethodologyId))
                              || (fieldReads.TryGetValue(b.MethodologyId, out var reads)
                                  && owned.Exists(f => reads.ReadsForeign(f.Column, aliasesByColumn.GetValueOrDefault(f.Column.Id)))));
                if (ownReader || readCodes.Contains(table.Code))
                {
                    result.Add(inPeriod.Key);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Розкладає перерахунок проєкту на документні задачі (P4 ФВ-9.8) і
    /// завершується, не чекаючи їх.
    /// </summary>
    /// <param name="scheduler">Планувальник.</param>
    /// <param name="request">Завдання проєкту з уже визначеним <c>ProjectId</c>.</param>
    /// <param name="progress">Канал прогресу батьківської задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Дочірня задача несе ВСЕ завдання батька, крім <c>DocumentId</c>: період,
    /// автора; погоджень перерахунку закритого періоду більше немає (D-468, 07.11): закритий
    /// період відмовляє гейт стану (<see cref="RefusedPeriodsAsync"/>) — шлях один, Reopen.
    /// <para>
    /// ⚠ Злиття, а не витіснення: повторний нічний запуск, поки документна задача
    /// ще в черзі, не ставить другу (<c>EnqueueCoalescedAsync</c>), і не перериває
    /// ту, що вже рахує. Ціль названого періоду — та сама, що в кнопки документа
    /// (<see cref="Ecr.Application.Documents.RecalculateDocumentHandler.TargetOf"/>):
    /// нічний і ручний перерахунок того самого документа й періоду — одна робота.
    /// </para>
    /// <para>
    /// ⚠ Батько не чекає дочірніх: задача, що тримає слот пулу в очікуванні задач
    /// того самого пулу, за малого пулу (черга в базі — 4 слоти) блокує саме тих,
    /// кого чекає. Стан кожного документа видно окремим рядком черги.
    /// </para>
    /// </remarks>
    private async Task FanOutAsync(
        IBackgroundJobScheduler scheduler,
        RecalculationRequest request,
        IJobProgress progress,
        CancellationToken ct)
    {
        var documentIds = await ProjectDocumentIdsAsync(request.ProjectId, ct).ConfigureAwait(false);
        var enqueued = 0;

        foreach (var documentId in documentIds)
        {
            // Позначка батька — щоб статус показував «розкладено N, виконано M».
            var child = request with
            {
                DocumentId = documentId,
                FanOutParentJobId = (progress as IJobIdentity)?.JobId,
            };

            // ✎ UAT-recalc-ux: документ, у якому за названий період подано/затверджено ВСЕ, не ставиться — дочірня
            // задача відмовила б 4221 і зробила б перерахунок проєкту «з помилками», хоча пропуск поданих аркушів —
            // штатна поведінка. Те саме рішення, що й на шляху запису (`RefusedPeriodsAsync`).
            //
            // ⛔ B81 (F13 #877): той самий виклик для АРХІВОВАНОГО проєкту кидав `ProjectArchived` (B69) — і валив
            // БАТЬКІВСЬКУ задачу посеред розкладу, хоча відмова тут штатна: архівні дані не перераховуються. Тепер
            // документ пропускається з рядком журналу, а батько завершується «розкладено N».
            if (child.PeriodKey is not null)
            {
                var skip = await FanOutSkipAsync(child, ct).ConfigureAwait(false);
                if (skip == RecalculationWriteDenial.ProjectArchived && logger is not null)
                {
                    LogArchivedProjectSkipped(logger, request.ProjectId, documentId);
                }

                if (skip != RecalculationWriteDenial.None)
                {
                    continue;
                }
            }

            enqueued++;

            var target = child.PeriodKey is { } period
                ? Ecr.Application.Documents.RecalculateDocumentHandler.TargetOf(documentId, new PeriodKey(period))
                : Ecr.Application.Documents.RecalculateDocumentHandler.YearTargetOf(documentId);

            await scheduler
                .EnqueueCoalescedAsync<IRecalculationJob>(target, child, ct, child.TriggeredByUserId)
                .ConfigureAwait(false);
        }

        await progress
            .ReportAsync(
                100,
                JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                    "jobs.recalcFannedOut",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["count"] = enqueued.ToString(CultureInfo.InvariantCulture),
                    })),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Причина запису журналу для затвердженого аркуша, перерахованого за перекриттям адміністратора.</summary>
    /// <param name="reason">Причина, введена адміністратором.</param>
    private static string OverrideChangeReason(string? reason)
    {
        var text = "recalc-approved-override: " + (reason ?? string.Empty).Trim();
        return text.Length <= 400 ? text : text[..400];
    }

    /// <summary>✎ Огляд L-4: стеля запису позначок перерваної задачі (<see cref="FlushSubmittedMarksAsync"/>).</summary>
    private static readonly TimeSpan FlushMarksTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Позначка «перераховано після подання» для задачі, перерваної до журналу аркушів (<see cref="SheetLogAsync"/>):
    /// скасування (витіснення кнопкою «Перерахувати», межа тривалості, зупинка служби), відкладення чи збій.
    /// </summary>
    /// <param name="request">Завдання.</param>
    /// <param name="changes">Закомічені зміни поданих аркушів: комірки формул (шлях запису — після коміту) і результати
    /// методологій періодів, чиє перемикання вже закомічене.</param>
    /// <remarks>
    /// <para>
    /// ⛔ e2e «SHEETNAME: copy marker» (21.11): кнопка «Перерахувати» (Exclusive) скасовує виконуваний автоперерахунок того
    /// самого документа й періоду. Фаза формул комітить комірки ОДРАЗУ, а позначку ставив лише журнал аркушів у самому кінці
    /// задачі. Скасована між ними задача лишала подані числа зміненими без позначки, а прогін, що її витіснив, змін уже не
    /// бачив (B79: позначка — лише за зміну) — погоджувач не дізнавався, що подане змінилося.
    /// </para>
    /// <para>
    /// Без токена задачі (він уже скасований) і без «пропущених затверджених»: їх журнал аркушів пише лише за прогін, що
    /// дійшов до кінця. Збій тут лише в журнал — початковий виняток (скасування, відкладення) не підміняється.
    /// </para>
    /// <para>
    /// ✎ Огляд L-4: власна стеля (<see cref="FlushMarksTimeout"/>), а не <c>CancellationToken.None</c>: зупинка служби чи
    /// завислий SQL не тримають скасовану задачу (і слот виконавця) без кінця. Вичерпано — той самий шлях, що збій: журнал.
    /// </para>
    /// </remarks>
    private async Task FlushSubmittedMarksAsync(RecalculationRequest request, SubmittedSheetChanges changes)
    {
        if (changes.Count == 0 || audit is null)
        {
            return;
        }

        using var bounded = new CancellationTokenSource(FlushMarksTimeout);
        try
        {
            foreach (var group in changes.Items.GroupBy(c => (c.DocumentId, c.PeriodKey)))
            {
                await SubmittedSheetRecalcLog
                    .WriteAsync(
                        db, audit, clock, group.Key.DocumentId, group.Key.PeriodKey,
                        [.. group.Select(c => (c.SheetDefId, c.SheetCopyId))], bounded.Token,
                        changedByUserId: request.TriggeredByUserId ?? 0, source: "interruptedRun")
                    .ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // Позначка — другорядне: скасування/відкладення не підміняється збоєм журналу.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFlushMarksFailed(logger ?? NullLogger<RecalculationJob>.Instance, request.DocumentId, changes.Count, ex);
        }
    }

    /// <summary>
    /// ✎ UAT-recalc-ux (слово людини 07.11: «це також має логуватися»): пише в журнал змін кожен пропущений
    /// затверджений аркуш і кожен перерахований поданий (по запису на аркуш × період, <c>SheetRecalc</c>).
    /// </summary>
    /// <param name="request">Завдання.</param>
    /// <param name="scopeByDocument">Періоди, які прогін рахував, за документами.</param>
    /// <param name="changes">✎ B79: подані аркуші, у яких прогін змінив хоч одне число.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="blockedReaders">✎ MS: затверджені аркуші читачів, яких не перераховано (див. <c>BindingPlan.BlockedReaders</c>), за документами.</param>
    /// <returns>Скільки пропущено і скільки поданих перераховано зі зміною чисел.</returns>
    /// <remarks>
    /// ⚠ Запис на АРКУШ, а не один на задачу: позначку «перераховано після подання» читає картка документа саме за
    /// аркушем і періодом (<c>RecalculationSkips.AuditCorrelation</c>), і саме цей запис пізніший за <c>SubmittedAt</c>.
    /// Аркуш області (<c>SheetDefId</c>) обмежує обидва переліки цим аркушем.
    /// <para>
    /// ⛔ B79 (слово людини 16.11): поданий аркуш отримує запис і позначку ЛИШЕ коли прогін справді змінив у ньому хоч
    /// одне збережене число (<see cref="SubmittedSheetChanges"/>) — на КОЖНОМУ шляху цієї задачі: нічний розклад, кнопка,
    /// автоперерахунок після збору й залежних. Доти позначався кожен поданий аркуш області, а нічний розклад через це
    /// поданих не чіпав узагалі. Прогін над незмінними входами поданому аркушу не лишає ні запису, ні позначки.
    /// </para>
    /// </remarks>
    private async Task<(int Skipped, int RecalculatedSubmitted)> SheetLogAsync(
        RecalculationRequest request, Dictionary<long, int[]> scopeByDocument, SubmittedSheetChanges changes,
        CancellationToken ct, Dictionary<long, IReadOnlySet<(int Period, int SheetDefId)>>? blockedReaders = null)
    {
        var entries = new List<StructureChangeRecord>();
        var skipped = 0;
        var recalculated = 0;
        var now = clock.UtcNow;

        foreach (var (documentId, scope) in scopeByDocument)
        {
            // ✎ B79: не-місячне число річної розкладки змінює прогін якоря, а позначку отримують подані місяці РОКУ —
            // вони можуть лежати поза областю прогону (кнопка за названий місяць, закритий якір B24 з порожньою областю).
            // Затверджені поза областю в «пропущені» не йдуть: прогін їх і не мав рахувати.
            var periods = scope.Union(changes.PeriodsOf(documentId)).ToArray();
            if (periods.Length == 0)
            {
                continue;
            }

            // ✎ F13 p218 #788: по СКОУПУ (аркуш × копія, RS) — подана копія перераховується з позначкою й записом журналу,
            // затверджена — пропускається із записом, так само як оригінал (правило людини 07.11).
            // ✎ B138: версія без погодження — нічого не «пропущено» й не «перераховано після подання».
            // ✎ MS: затверджений аркуш читача, якого не перераховано через затвердження, позначається і поза областю аркуша.
            var blocked = blockedReaders is not null && blockedReaders.TryGetValue(documentId, out var blockedSet)
                ? blockedSet
                : new HashSet<(int Period, int SheetDefId)>();
            var blockedSheetIds = blocked.Select(b => b.SheetDefId).Distinct().ToList();
            var rows = await db.ApprovalStates
                .AsNoTracking()
                .ExceptWorkflowNone(db)
                .Where(a => a.DocumentId == documentId && periods.Contains(a.PeriodKey)
                            && (request.SheetDefId == null || a.SheetDefId == request.SheetDefId
                                || blockedSheetIds.Contains(a.SheetDefId))
                            && (a.Status == Domain.Enums.DocumentStatus.Approved
                                || a.Status == Domain.Enums.DocumentStatus.Submitted))
                .Join(db.SheetDefs, a => a.SheetDefId, s => s.Id, (a, s) => new { a.SheetDefId, a.SheetCopyId, a.PeriodKey, a.Status, s.TemplateVersionId, s.Code })
                .OrderBy(r => r.PeriodKey).ThenBy(r => r.SheetDefId).ThenBy(r => r.SheetCopyId)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                var approvedRow = row.Status == Domain.Enums.DocumentStatus.Approved;

                // ✎ B80 N5: перекриття адміністратора — затверджений аркуш НЕ пропущено, а перераховано; запис журналу
                // називає, хто, чому й який затверджений аркуш (рішення людини 19.11: «все логується»).
                var approvedRecalculated = approvedRow && request.IncludeApproved;

                if (request.SheetDefId is { } areaSheet && row.SheetDefId != areaSheet
                    && !(approvedRow && row.SheetCopyId is null && blocked.Contains((row.PeriodKey, row.SheetDefId))))
                {
                    continue;
                }

                // ✎ B79: поданий аркуш, у якому прогін не змінив жодного числа, — без запису й без позначки.
                if (!approvedRow && !changes.Contains(documentId, row.PeriodKey, row.SheetDefId, row.SheetCopyId))
                {
                    continue;
                }

                // ✎ B79: затверджений аркуш періоду поза областю прогону (період додано лише заради поданих місяців року).
                if (approvedRow && !scope.Contains(row.PeriodKey))
                {
                    continue;
                }

                if (approvedRow && !approvedRecalculated)
                {
                    skipped++;
                }
                else
                {
                    recalculated++;
                }

                entries.Add(new StructureChangeRecord(
                    now,
                    TemplateVersionId: row.TemplateVersionId,
                    EntityType: RecalculationSkips.AuditEntityType,
                    EntityId: row.SheetDefId,
                    ChangeClass: Domain.Enums.ChangeClass.Safe,
                    Operation: approvedRecalculated
                        ? RecalculationSkips.AuditRecalculatedApproved
                        : approvedRow ? RecalculationSkips.AuditSkippedApproved : RecalculationSkips.AuditRecalculatedAfterSubmission,
                    OldJson: null,
                    NewJson: JsonSerializer.Serialize(new
                    {
                        documentId,
                        sheetDefId = row.SheetDefId,
                        sheetCopyId = row.SheetCopyId,
                        sheetCode = row.Code,
                        periodKey = row.PeriodKey,
                        state = row.Status.ToString(),
                        reason = approvedRecalculated ? "approvedOverride" : approvedRow ? "approved" : "recalculatedAfterSubmission",
                        overrideReason = approvedRecalculated ? request.OverrideReason : null,
                    }),
                    ChangeReason: approvedRecalculated
                        ? OverrideChangeReason(request.OverrideReason)
                        : approvedRow ? "recalc-skipped-approved" : "recalc-after-submission",
                    ChangedByUserId: approvedRecalculated ? request.OverrideUserId ?? request.TriggeredByUserId ?? 0 : request.TriggeredByUserId ?? 0,
                    CorrelationId: RecalculationSkips.AuditCorrelation(documentId, row.SheetDefId, row.PeriodKey, row.SheetCopyId)));
            }
        }

        if (audit is not null && entries.Count > 0)
        {
            await audit.WriteStructureChangesAsync(entries, ct).ConfigureAwait(false);
        }

        return (skipped, recalculated);
    }

    /// <summary>
    /// Чи пропустити документ у розкладі проєкту: у названому періоді подано/затверджено все (рахувати нічого) або
    /// проєкт архівований чи архівується (B81) — <see cref="RecalculationWriteDenial.None"/>, якщо ставити.
    /// </summary>
    private async Task<RecalculationWriteDenial> FanOutSkipAsync(RecalculationRequest child, CancellationToken ct)
    {
        try
        {
            _ = await RefusedPeriodsAsync(child, ct).ConfigureAwait(false);
            return RecalculationWriteDenial.None;
        }
        catch (Ecr.Application.Errors.BusinessRuleException refused)
            when (refused.Details is { } details
                  && details.TryGetValue("denial", out var denial)
                  && (denial as string == nameof(RecalculationWriteDenial.SheetsSubmitted)
                      || denial as string == nameof(RecalculationWriteDenial.ProjectArchived)))
        {
            return denial as string == nameof(RecalculationWriteDenial.ProjectArchived)
                ? RecalculationWriteDenial.ProjectArchived
                : RecalculationWriteDenial.SheetsSubmitted;
        }
    }

    /// <summary>Повний перерахунок формул шаблону для документа й періодів завдання.</summary>
    /// <returns>Скільки комірок перераховано.</returns>
    /// <remarks>
    /// ⛔ Повний, а не інкрементний. Інкрементний шлях
    /// (<c>PatchCellsHandler</c> → <c>IFormulaRecalculationJob</c>) бере
    /// формули з набору змінених комірок, тож формула, ДОДАНА в шаблон після
    /// введення даних, не потрапляє в нього ніколи — і залишалася б
    /// непорахованою нескінченно.
    ///
    /// ⚠ Періоди приходять готовим списком із <see cref="ScopePeriodsAsync"/>
    /// — уже відфільтровані гейтом запису й упорядковані ЗА ЗРОСТАННЯМ.
    /// Формула шаблону має право читати попередній період
    /// (<c>[Period:-1]</c>), і зворотний порядок порахував би лютий зі старого
    /// січня, а потім січень — правильно, але вже нікому.
    /// </remarks>
    /// <param name="request">Завдання перерахунку.</param>
    /// <param name="periods">Періоди в скоупі запису, за зростанням.</param>
    /// <param name="submittedChanges">✎ B79: куди скласти подані аркуші, де записано змінену комірку формули.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="annualOnly">✎ LAND-YEAR B24: закриті якорі річної розкладки → місяці їхнього року (лише не-місячні цілі).</param>
    /// <param name="changedOut">✎ PERF-01: куди скласти змінені колонки за періодом (дописування читачів у план ланцюжка).</param>
    private async Task<int> FormulasAsync(
        RecalculationRequest request, IReadOnlyList<int> periods, SubmittedSheetChanges submittedChanges,
        CancellationToken ct, IReadOnlyDictionary<int, IReadOnlyList<int>>? annualOnly = null,
        Dictionary<int, HashSet<(int TableDefId, int ColumnDefId)>>? changedOut = null)
    {
        var written = 0;
        var changedByPeriod = changedOut ?? new Dictionary<int, HashSet<(int TableDefId, int ColumnDefId)>>();

        foreach (var period in periods)
        {
            var changed = new HashSet<(int TableDefId, int ColumnDefId)>();
            changedByPeriod[period] = changed;

            // ✎ B79: подані аркуші пише кожен прогін (нічний теж); затверджені — ніколи. Шлях запису повертає ті подані
            // аркуші, де він справді записав змінену комірку, — другого читання для цього не треба.
            var submittedWritten = new HashSet<(int SheetDefId, long? CopyId)>();

            // Річна розкладка: не-місячне число (якір, агрегат року) — поданим МІСЯЦЯМ року, а не періоду прогону.
            var submittedYearWritten = new HashSet<(int PeriodKey, int SheetDefId)>();

            // ⛔ Огляд M-1: злиття — у `finally`. Прогін періоду йде по «видах» (оригінал, кожна копія), і кожен комітить
            // сам; набори наповнюються лише ПІСЛЯ коміту виду. Скасування чи відкладення посеред виду №2 інакше губило б
            // закомічені зміни виду №1 — і аварійний запис позначок (FlushSubmittedMarksAsync) їх не бачив би.
            try
            {
                written += await formulas
                    .RecalculateAllAsync(
                        request.DocumentId, new PeriodKey(period), ct, request.SheetDefId, changed,
                        writeSubmitted: true,
                        writeApproved: request.IncludeApproved,
                        annualPeriods: annualOnly?.GetValueOrDefault(period),
                        submittedWritten: submittedWritten,
                        submittedYearWritten: submittedYearWritten)
                    .ConfigureAwait(false);
            }
            finally
            {
                submittedChanges.AddRange(request.DocumentId, period, submittedWritten);
                foreach (var (month, sheetDefId) in submittedYearWritten)
                {
                    submittedChanges.Add(request.DocumentId, month, sheetDefId, sheetCopyId: null);
                }
            }
        }

        // ⛔ WW-03: прохід за зростанням коректний для читачів ПІЗНІШЕ за джерело (`[Period:-1]`,
        // `[Period:#k]` з k раніше). Читач РАНІШЕ за джерело (`[Period:#k]`, k пізніше) порахований
        // від ще не оновленого джерела — другий, цільовий прохід: coalesced-задача періоду-читача
        // лише на формули з міжперіодним ребром. Рахується один раз після всіх періодів.
        if (fanOut is not null)
        {
            foreach (var (period, changed) in changedByPeriod)
            {
                if (changed.Count > 0)
                {
                    await fanOut
                        .EnqueueReadersAsync(
                            request.DocumentId, new PeriodKey(period), changed, onlyEarlierReaders: true, hops: 1, ct)
                        .ConfigureAwait(false);
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Періоди документа, у які цей прогін має право писати, ЗА ЗРОСТАННЯМ.
    /// </summary>
    /// <param name="request">Завдання; <c>PeriodKey = null</c> — увесь рік.</param>
    /// <param name="refused">Періоди, у які запис заборонений (ФВ-9.7, ФВ-9.17).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключі періодів; порожньо — записувати нема куди.</returns>
    /// <remarks>
    /// ⚠ Періоди беруться з ЕКЗЕМПЛЯРІВ таблиць, а не з <c>request.PeriodKey</c>:
    /// той може бути <c>null</c> — «повний рік», — і <c>new PeriodKey(0)</c> тоді
    /// виглядав би як звичайний період, у якому просто нічого немає
    /// (<c>A7-28</c>, <c>PeriodKey.IsValid</c>).
    ///
    /// ⛔ Метод спільний для ОБОХ конвеєрів (формули шаблону й методології), і
    /// саме в цьому суть виправлення: доти цей розрахунок жив усередині фази
    /// формул, а фаза методологій не робила його взагалі — і тому єдина на всі
    /// періоди отримувала ключ із завдання, тобто нуль для річного прогону.
    /// Одне джерело скоупу означає, що обидві фази не можуть розійтися в тому,
    /// які періоди прогін вважає своїми.
    ///
    /// ⚠ Q-331: коли <c>SheetDefId</c> заданий, скоуп звужується до періодів, у
    /// яких є ХОЧ ОДНА таблиця ЦЬОГО аркуша — період, де аркуш порожній,
    /// однаково не запише жодної комірки; фільтр лише економить прогони, що
    /// напевно нічого не запишуть.
    ///
    /// ⛔ Періоди, у які писати не можна, ВИЛУЧАЮТЬСЯ зі скоупу запису
    /// (ФВ-9.7, ФВ-9.17). Для явно названого періоду сюди вже не доходить —
    /// <see cref="RefusedPeriodsAsync"/> відмовив винятком; це фільтр для
    /// прогону «на весь рік» (нічний розклад, <c>PeriodKey = null</c>), де
    /// відмовляти цілим прогоном не можна: у будь-якому році після січня є
    /// закриті періоди, і нічний перерахунок перестав би працювати назавжди.
    ///
    /// ⚠ ЗА ЗРОСТАННЯМ, і це не косметика. Формула шаблону має право читати
    /// попередній період (<c>[Period:-1]</c>), і зворотний порядок порахував
    /// би лютий зі старого січня, а потім січень — правильно, але вже нікому.
    /// </remarks>
    internal async Task<IReadOnlyList<int>> ScopePeriodsAsync(
        RecalculationRequest request, IReadOnlySet<int> refused, CancellationToken ct)
    {
        // ✎ LAND-YEAR B59: «увесь рік» кнопки річного документа — лише ключі свого календарного року.
        var fromKey = request.FromKey;
        var toKey = request.ToKey;

        Task<List<int>> ScopesAsync(IQueryable<Ecr.Domain.Entities.Documents.TableInstance> instances)
        {
            var scopesQuery = instances
                .Where(i => request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey)
                .Where(i => i.PeriodKeyValue >= fromKey && i.PeriodKeyValue <= toKey);

            if (request.SheetDefId is { } scopeSheetId)
            {
                scopesQuery = scopesQuery.Where(i =>
                    db.TableDefs.Any(td => td.Id == i.TableDefId && td.SheetDefId == scopeSheetId));
            }

            return scopesQuery
                .Select(i => i.PeriodKeyValue)
                .Distinct()
                // За зростанням ДО стелі: на межі беруться найраніші періоди —
                // ті, від яких рахуються наступні (`[Period:-1]`), — а не
                // довільні (EF 10102).
                .OrderBy(key => key)
                .Take(MaxBindings)
                .ToListAsync(ct);
        }

        // ⚠ O3d: з ключем партиції (DocumentInstancesQuery). Порожньо — колишній пошук
        // без ключа: документ, чиї екземпляри лежать лише в періодах без рядка в
        // doc.Period, дає той самий скоуп, що й до O3d. Метод `internal` саме заради тесту
        // цього шляху (AN-13 D4, RecalculationScopeFallbackTests).
        var scopes = await ScopesAsync(DocumentInstancesQuery(db, request.DocumentId)).ConfigureAwait(false);
        if (scopes.Count == 0)
        {
            scopes = await ScopesAsync(db.TableInstances.AsNoTracking().Where(i => i.DocumentId == request.DocumentId))
                .ConfigureAwait(false);
        }

        return [.. scopes.Where(key => !refused.Contains(key)).OrderBy(key => key)];
    }

    /// <summary>
    /// Екземпляри таблиць документа з ключем партиції (O3d, I2-2 ФВ-9.8).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <returns>Незавершений запит; фільтри й проєкцію добирає викликач.</returns>
    /// <remarks>
    /// ⛔ Ключі <c>doc.TableInstance</c> — <c>(PeriodKey, …)</c> в усіх індексах
    /// (<c>PK</c> — <c>PeriodKey, Id</c>; <c>UQ</c> — <c>PeriodKey, DocumentId, TableDefId</c>),
    /// і предикат лише за <c>DocumentId</c> сканує <c>UQ_TableInstance</c> у всіх 25
    /// партиціях. <c>PeriodKey IN (SELECT PeriodKey FROM doc.Period)</c> — як у
    /// <c>RowStore.TableInstancesByIdQuery</c> — дає по seek'у на період. Замір на
    /// <c>EcrPerfI2</c> (док 326, фактичний план): скоуп періодів 1 984 читання / 70 мс ЦП →
    /// 66 / 0 мс (24 партиції з seek'ом замість скану 25).
    /// <para>⚠ Екземпляр, чийого періоду немає в <c>doc.Period</c>, цей запит не знайде
    /// (FK на <c>doc.Period</c> немає) — тому викликачі мають запасний шлях без ключа.</para>
    /// </remarks>
    public static IQueryable<Ecr.Domain.Entities.Documents.TableInstance> DocumentInstancesQuery(
        EcrDbContext db, long documentId)
    {
        ArgumentNullException.ThrowIfNull(db);

        return db.TableInstances
            .AsNoTracking()
            .Where(i => db.Periods.Select(p => p.PeriodKeyValue).Contains(i.PeriodKeyValue)
                        && i.DocumentId == documentId);
    }

    /// <summary>Прив'язки методологій до таблиць документа, РОЗКЛАДЕНІ ЗА ПЕРІОДАМИ.</summary>
    /// <param name="request">Завдання перерахунку.</param>
    /// <param name="periods">Періоди в скоупі запису, за зростанням.</param>
    /// <param name="skippedSheets">Пропущені (затверджені; ✎ B79 — подані рахуються) аркуші за періодами; їхні таблиці не рахуються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки за ключем періоду; періоду без прив'язок у мапі немає.</returns>
    /// <remarks>
    /// Прив'язка живе в <c>cfg.CalculationBinding</c> і посилається на
    /// <c>TableDefId</c> — опис таблиці. Прогін працює з ЕКЗЕМПЛЯРАМИ, тому
    /// опис розгортається в екземпляри цього документа й періоду.
    ///
    /// ⛔ Саме МАПА ЗА ПЕРІОДАМИ, а не один плаский список. Плаский список був
    /// половиною дефекту річного прогону: він чесно містив екземпляри всіх
    /// місяців року, але віддавався оркестраторові разом з ОДНИМ ключем
    /// періоду — і той різнорідний набір рахувався так, ніби весь належить
    /// одному періоду. Версія методології резолвиться за датою періоду
    /// (ФВ-9.3), а входи (<c>CalculationInputBuilder</c>) читаються зрізом
    /// періоду, тож «період прив'язки» — не метадані, а частина самого
    /// розрахунку.
    /// </remarks>
    private async Task<BindingPlan> BindingsAsync(
        RecalculationRequest request, IReadOnlyList<int> periods,
        Dictionary<int, HashSet<int>> skippedSheets, CancellationToken ct)
    {
        // ⚠ Q-331: методологія прив'язана до `TableDefId`, тобто до конкретної
        // таблиці — і, транзитивно, до аркуша, якому та таблиця належить
        // (`TableDef.SheetDefId`). Звузити прив'язки до аркуша тут БЕЗПЕЧНО
        // так само, як і формули шаблону вище: методологія рахує РЕЗУЛЬТАТ у
        // `calc.CalculationResult` для таблиці цього аркуша, а вхідні дані
        // (`CalculationInputBuilder`) читає з `doc.CellValue` без огляду на
        // те, прив'язку якого аркуша перераховує цей прогін.
        // ⛔ Той самий гейт і для МЕТОДОЛОГІЙ: їхній результат лягає в
        // `calc.CalculationResult` для екземпляра таблиці конкретного періоду,
        // тож екземпляр закритого періоду мусить випасти зі списку прив'язок,
        // а не лише з фази формул. Тепер це не окремий фільтр, а наслідок
        // спільного скоупу: `periods` уже пройшли `ScopePeriodsAsync`, тобто
        // закритого січня в них немає. Дві копії фільтра, які колись стояли
        // тут і у фазі формул, розійтися більше не можуть.
        var scopeKeys = periods.ToList();

        // ⚠ O3d: ключ партиції тут УЖЕ є — `PeriodKey IN (@scopeKeys1, …)` (EF 10 —
        // окремі параметри): фактичний план на EcrPerfI2, док 326 — 12 seek'ів, 40 читань.
        // Додатковий `IN (SELECT PeriodKey FROM doc.Period)` дав би 24 seek'и / 64 читання,
        // тобто гірше, — тому не доданий.
        var instances = await db.TableInstances
            .AsNoTracking()
            .OriginalOnly()   // RS: методології прив'язані до таблиць оригіналу; копії рахує лише фаза формул
            .Where(i => i.DocumentId == request.DocumentId
                        && scopeKeys.Contains(i.PeriodKeyValue))
            .OrderBy(i => i.PeriodKeyValue)
            .ThenBy(i => i.Id)
            .Take(MaxBindings)
            .Select(i => new InstanceRow(
                i.Id, i.TableDefId, i.PeriodKeyValue,
                db.TableDefs.Where(td => td.Id == i.TableDefId).Select(td => td.SheetDefId).FirstOrDefault()))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return BindingPlan.Empty;
        }

        var tableDefIds = instances.Select(i => i.TableDefId).Distinct().ToList();

        var bindings = await db.CalculationBindings
            .AsNoTracking()
            .Where(b => b.IsActive && tableDefIds.Contains(b.TableDefId))
            .OrderBy(b => b.Id)
            .Take(MaxBindings)
            .Select(b => new BindingRow(b.TableDefId, b.MethodologyId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⛔ RC15 (P2-A/P2-C): які таблиці прогін ПЕРЕРАХОВУЄ («активні») — а які ні:
        //  - область аркуша: лише таблиці цього аркуша;
        //  - весь документ: усі, КРІМ пропущених (поданих/затверджених) аркушів періоду.
        // Методологія, чия прив'язка є хоч в одній активній таблиці періоду, рахується ЦІЛКОМ (усі її
        // прив'язки в періоді): результат лягає за методологією й рядком, а не за таблицею, тож відокремити
        // рядки «чужого» аркуша від «своїх» неможливо, і перенесення старих рядків разом із новими подвоїло б
        // їх. Методології, що прив'язані ЛИШЕ до неактивних таблиць, прогін не рахує — їхні результати
        // переносяться (`ResultCarryOver`) з попереднього актуального прогону.
        bool IsActive(InstanceRow row)
            => (request.SheetDefId is not { } scope || row.SheetDefId == scope)
               && !(skippedSheets.TryGetValue(row.PeriodKeyValue, out var skipped) && skipped.Contains(row.SheetDefId));

        var methodologiesByTable = bindings
            .GroupBy(b => b.TableDefId)
            .ToDictionary(g => g.Key, g => g.Select(b => b.MethodologyId).Distinct().ToList());

        var touchedByPeriod = instances
            .Where(IsActive)
            .GroupBy(i => i.PeriodKeyValue)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(i => methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? []).ToHashSet());

        // ✎ MS (план §13.1, §13.9): методології-скрипти, що ЧИТАЮТЬ таблицю активного екземпляра, і всі, хто читає
        // результати перерахованих, — теж перераховуються, АЛЕ не на затверджених аркушах (D-328, D-493, B79):
        // читач, що живе лише на затвердженому аркуші, лишається зі своїми числами (їх переносить CarryOver), а аркуш
        // отримує ту саму позначку «пропущено, затверджений» (SheetLogAsync), що й формульний залежний.
        var blockedReaders = new HashSet<(int Period, int SheetDefId)>();
        await ExtendTouchedWithScriptReadersAsync(
                instances, IsActive, skippedSheets, methodologiesByTable, touchedByPeriod, blockedReaders, ct)
            .ConfigureAwait(false);

        var carryByPeriod = new Dictionary<int, IReadOnlyCollection<int>>();
        var partialByPeriod = new Dictionary<int, IReadOnlySet<long>>();
        foreach (var group in instances.GroupBy(i => i.PeriodKeyValue))
        {
            var touched = touchedByPeriod.GetValueOrDefault(group.Key) ?? [];
            var carry = group
                .SelectMany(i => methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? [])
                .Where(id => !touched.Contains(id))
                .Distinct()
                .Order()
                .ToList();
            if (carry.Count > 0)
            {
                carryByPeriod[group.Key] = carry;

                // Таблиця, де одна методологія рахується (спільна з активним аркушем), а інша переноситься: рядки,
                // які закрила перенесена, цей прогін бачить «без правила».
                var partial = group
                    .Where(i => (methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? []).Any(id => !touched.Contains(id)))
                    .Select(i => i.Id)
                    .ToHashSet();
                if (partial.Count > 0)
                {
                    partialByPeriod[group.Key] = partial;
                }
            }
        }

        // ⚠ `Distinct` — усередині групи періоду, а не над плоским набором:
        // однакова пара (екземпляр, методологія) неможлива в двох періодах,
        // бо екземпляр належить рівно одному періоду, але робити `Distinct`
        // після групування чесніше — воно тоді означає рівно те, що написано.
        // ✎ D-557: СПІЛЬНА методологія (прив'язана і до затвердженої, і до незатвердженої таблиці) рахується на
        // незатверджених, а затверджені екземпляри лишаються зі старими числами: результат має `TableInstanceId`, тож
        // прив'язку затвердженого екземпляра прогін не рахує, а її результати переносяться (`CarryOverInstances`).
        // ⚠ Результати до D-401 не мають `TableInstanceId` (NULL) — їх не віднести до таблиці, тож затверджений екземпляр
        // без власних результатів у документі, де такі «безприв'язні» є, лишається за старим правилом (рахується).
        var approvedIds = instances.Where(i => skippedSheets.TryGetValue(i.PeriodKeyValue, out var approved)
                                               && approved.Contains(i.SheetDefId)).Select(i => i.Id).ToList();
        // ⛔ Джерело результатів — те саме, що в `CarryOverResultsAsync` (`CurrentRunSelection`: документний прогін
        // періоду має перевагу над проєктним). ✎ D-560: з перечитуванням прогонів (прибирання витіснених одразу).
        var (attributed, legacyPeriods) = approvedIds.Count == 0
            ? ([], [])
            : await CurrentRunSelection.ApprovedAttributionAsync(
                    db,
                    request.DocumentId,
                    [.. instances.Where(i => approvedIds.Contains(i.Id)).Select(i => i.PeriodKeyValue).Distinct()],
                    approvedIds,
                    ct)
                .ConfigureAwait(false);

        bool IsApproved(InstanceRow row)
            => skippedSheets.TryGetValue(row.PeriodKeyValue, out var approved) && approved.Contains(row.SheetDefId)
               && (attributed.Contains(row.Id) || !legacyPeriods.Contains(row.PeriodKeyValue));

        var carryInstances = new Dictionary<int, (HashSet<int> Methodologies, HashSet<long> Instances)>();
        foreach (var i in instances.Where(IsApproved))
        {
            var touchedHere = touchedByPeriod.GetValueOrDefault(i.PeriodKeyValue);
            var shared = (methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? [])
                .Where(m => touchedHere?.Contains(m) ?? false)
                .ToList();
            if (shared.Count == 0)
            {
                continue;
            }

            if (!carryInstances.TryGetValue(i.PeriodKeyValue, out var entry))
            {
                carryInstances[i.PeriodKeyValue] = entry = ([], []);
            }

            entry.Methodologies.UnionWith(shared);
            entry.Instances.Add(i.Id);
        }

        var recomputedOnSubmitted = 0;
        var byPeriod = instances
            .Where(i => !IsApproved(i))
            .SelectMany(i => bindings
                .Where(b => b.TableDefId == i.TableDefId
                            && (touchedByPeriod.GetValueOrDefault(i.PeriodKeyValue)?.Contains(b.MethodologyId) ?? false))
                .Select(b => new PeriodBindingRow(
                    i.PeriodKeyValue, new CalculationBindingRef(i.Id, b.MethodologyId))))
            .GroupBy(row => row.PeriodKeyValue)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CalculationBindingRef>)
                    [.. group.Select(row => row.Binding).Distinct()]);

        // Чесне попередження: спільна методологія перерахована й на ПОДАНОМУ аркуші (рахунок — лише для журналу).
        foreach (var i in instances.Where(i => !IsActive(i)))
        {
            recomputedOnSubmitted += byPeriod.TryGetValue(i.PeriodKeyValue, out var list)
                ? list.Count(b => b.TableInstanceId == i.Id)
                : 0;
        }

        // D2-03: аркуші, чиї таблиці прогін перерахував методологіями, — їх перевіряє `GuardCompletionAsync`.
        var activeSheets = instances
            .Where(i => IsActive(i)
                        && byPeriod.TryGetValue(i.PeriodKeyValue, out var periodBindings)
                        && periodBindings.Any(b => b.TableInstanceId == i.Id))
            .GroupBy(i => i.PeriodKeyValue)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<int>)[.. g.Select(i => i.SheetDefId).Distinct().Order()]);

        return new BindingPlan(byPeriod, carryByPeriod, recomputedOnSubmitted, activeSheets, partialByPeriod, blockedReaders)
        {
            CarryOverInstances = carryInstances.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyCollection<int>)[.. pair.Value.Methodologies.Order()],
                EqualityComparer<int>.Default),
            CarryOverInstanceIds = carryInstances.ToDictionary(
                pair => pair.Key, pair => (IReadOnlyCollection<long>)[.. pair.Value.Instances.Order()]),
        };
    }

    /// <summary>
    /// Додає до «зачеплених» методологій періоду (а) ті, чиї опубліковані версії-скрипти читають код таблиці будь-якого
    /// активного екземпляра цього періоду (<c>calc.MethodologyReadTable</c>), і (б) транзитивно всіх, хто читає
    /// результати вже зачеплених (<c>calc.MethodologyDependency</c>).
    /// </summary>
    /// <param name="instances">Екземпляри таблиць документа в скоупі.</param>
    /// <param name="isActive">Чи екземпляр перераховується цим прогоном.</param>
    /// <param name="approvedSheets">Затверджені аркуші за періодами (<see cref="ApprovedSheetsAsync"/>): їх не перераховує жоден прогін.</param>
    /// <param name="methodologiesByTable">Методології, прив'язані до таблиць (за <c>TableDefId</c>).</param>
    /// <param name="touchedByPeriod">Зачеплені методології за періодами; доповнюється на місці.</param>
    /// <param name="blockedReaders">
    /// Затверджені аркуші (період, аркуш) із читачами, які довелося б перерахувати, але затвердження не дозволяє: для них
    /// <see cref="SheetLogAsync"/> пише позначку «пропущено» навіть коли аркуш поза областю прогону.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Формульний документ платить двома дрібними читаннями на прогін (коди таблиць, читачі) — ребер і читачів
    /// там немає, і множини лишаються без змін. Читачі, прив'язок яких у документі нема, у перелік прив'язок не
    /// потрапляють: його складають прив'язки, а не цей набір.
    /// <para>
    /// ⛔ Затверджений аркуш не перераховується (D-328, D-493: пропускаються лише затверджені; B79). Читач потрапляє в
    /// набір періоду, лише коли має прив'язку на НЕзатвердженому екземплярі цього періоду (аркуш поза областю прогону —
    /// так, затверджений — ні); через не доданого читача ланцюг далі не йде: його числа не змінилися. Читач, прив'язаний
    /// і до затвердженої, і до незатвердженої таблиці, рахується цілком, як і кожна спільна методологія (RC15, D-328).
    /// </para>
    /// </remarks>
    private async Task ExtendTouchedWithScriptReadersAsync(
        IReadOnlyList<InstanceRow> instances,
        Func<InstanceRow, bool> isActive,
        Dictionary<int, HashSet<int>> approvedSheets,
        Dictionary<int, List<int>> methodologiesByTable,
        Dictionary<int, HashSet<int>> touchedByPeriod,
        HashSet<(int Period, int SheetDefId)> blockedReaders,
        CancellationToken ct)
    {
        var active = instances.Where(isActive).ToList();
        if (active.Count == 0 || touchedByPeriod.Count == 0)
        {
            return;
        }

        var defIds = active.Select(i => i.TableDefId).Distinct().ToList();
        var codeById = await db.TableDefs
            .AsNoTracking()
            .Where(t => defIds.Contains(t.Id))
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.Code })
            .Take(MaxBindings)
            .ToDictionaryAsync(t => t.Id, t => t.Code, ct)
            .ConfigureAwait(false);

        var store = new ScriptRuntimeStore(db);
        var readers = await store
            .GetReadersOfTablesAsync([.. codeById.Values.Distinct(StringComparer.OrdinalIgnoreCase)], ct)
            .ConfigureAwait(false);
        var edges = await store.GetDependencyEdgesAsync(ct).ConfigureAwait(false);
        if (readers.Count == 0 && edges.Count == 0)
        {
            return;
        }

        var readersOfSource = edges.ToLookup(e => e.ToMethodologyId, e => e.FromMethodologyId);
        foreach (var (period, touched) in touchedByPeriod)
        {
            var codes = active
                .Where(i => i.PeriodKeyValue == period)
                .Select(i => codeById.GetValueOrDefault(i.TableDefId))
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Методології, прив'язані до НЕзатверджених екземплярів періоду: лише їх можна перераховувати.
            var recalculable = instances
                .Where(i => i.PeriodKeyValue == period
                            && !(approvedSheets.TryGetValue(period, out var approved) && approved.Contains(i.SheetDefId)))
                .SelectMany(i => methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? [])
                .ToHashSet();
            var blocked = new HashSet<int>();
            foreach (var reader in readers.Where(r => codes.Contains(r.TableCode)))
            {
                if (recalculable.Contains(reader.MethodologyId))
                {
                    touched.Add(reader.MethodologyId);
                }
                else if (!touched.Contains(reader.MethodologyId))
                {
                    blocked.Add(reader.MethodologyId);
                }
            }

            var frontier = new Queue<int>(touched);
            while (frontier.TryDequeue(out var source))
            {
                foreach (var reader in readersOfSource[source])
                {
                    if (recalculable.Contains(reader))
                    {
                        if (touched.Add(reader))
                        {
                            frontier.Enqueue(reader);
                        }
                    }
                    else if (!touched.Contains(reader))
                    {
                        blocked.Add(reader);
                    }
                }
            }

            foreach (var i in instances.Where(i => i.PeriodKeyValue == period
                                                   && approvedSheets.TryGetValue(period, out var approved)
                                                   && approved.Contains(i.SheetDefId)
                                                   && (methodologiesByTable.GetValueOrDefault(i.TableDefId) ?? []).Any(m => blocked.Contains(m) && !touched.Contains(m))))
            {
                blockedReaders.Add((period, i.SheetDefId));
            }
        }
    }

    /// <summary>Прив'язки методологій за періодами, що переносити з попереднього прогону, і лічильник для журналу.</summary>
    /// <param name="Bindings">Прив'язки, які рахує прогін, за ключем періоду.</param>
    /// <param name="CarryOver">Методології, чиї результати переносяться з попереднього актуального прогону, за періодом.</param>
    /// <param name="RecomputedOnInactive">
    /// Скільки прив'язок неактивних (поза областю/пропущених) таблиць перераховано через спільну методологію.
    /// </param>
    /// <param name="ActiveSheets">
    /// D2-03: аркуші, чиї таблиці прогін перераховує методологіями, за періодом (за зростанням Id).
    /// </param>
    /// <param name="BlockedReaders">
    /// Затверджені аркуші (період, аркуш), де читач результатів/таблиць перерахованого джерела НЕ перераховано, бо аркуш затверджено.
    /// </param>
    /// <param name="PartialTables">
    /// Екземпляри таблиць періоду, де хоч одна прив'язана методологія переноситься, а не рахується. Їхні рядки прогін
    /// бачить не всіма методологіями, тож діагностика рядків (без правила, відхилені) і заміна проблем таблиці для них
    /// не робиться — лишаються проблеми попереднього повного проходу (live 11.11, doc 96: хибне NoMatchingRule).
    /// </param>
    private sealed record BindingPlan(
        IReadOnlyDictionary<int, IReadOnlyList<CalculationBindingRef>> Bindings,
        IReadOnlyDictionary<int, IReadOnlyCollection<int>> CarryOver,
        int RecomputedOnInactive,
        IReadOnlyDictionary<int, IReadOnlyList<int>> ActiveSheets,
        IReadOnlyDictionary<int, IReadOnlySet<long>> PartialTables,
        IReadOnlySet<(int Period, int SheetDefId)> BlockedReaders)
    {
        /// <summary>✎ D-557: спільні методології періоду, чиї результати ЗАТВЕРДЖЕНИХ екземплярів переносяться (а не рахуються).</summary>
        public IReadOnlyDictionary<int, IReadOnlyCollection<int>> CarryOverInstances { get; init; }
            = ReadOnlyDictionary<int, IReadOnlyCollection<int>>.Empty;

        /// <summary>✎ D-557: ті затверджені екземпляри таблиць, за якими переносяться <see cref="CarryOverInstances"/>.</summary>
        public IReadOnlyDictionary<int, IReadOnlyCollection<long>> CarryOverInstanceIds { get; init; }
            = ReadOnlyDictionary<int, IReadOnlyCollection<long>>.Empty;

        /// <summary>Порожній план: нічого не рахується й не переноситься.</summary>
        public static readonly BindingPlan Empty = new(
            ReadOnlyDictionary<int, IReadOnlyList<CalculationBindingRef>>.Empty,
            ReadOnlyDictionary<int, IReadOnlyCollection<int>>.Empty,
            0,
            ReadOnlyDictionary<int, IReadOnlyList<int>>.Empty,
            ReadOnlyDictionary<int, IReadOnlySet<long>>.Empty,
            new HashSet<(int Period, int SheetDefId)>());
    }

    /// <summary>Аркуші документа, які прогін періоду перерахував методологіями (D2-03).</summary>
    /// <param name="DocumentId">Документ.</param>
    /// <param name="Sheets">Аркуші за зростанням Id — у цьому порядку беруться блокування.</param>
    /// <param name="Bindings">✎ B79: прив'язки (екземпляр × методологія), які прогін періоду перерахував для документа.</param>
    private sealed record SheetGuard(
        long DocumentId, IReadOnlyList<int> Sheets, IReadOnlyList<CalculationBindingRef> Bindings);

    /// <summary>
    /// D2-03: перевірка перед перемиканням актуальності прогону періоду — у ТІЙ САМІЙ
    /// транзакції (<see cref="RunCalculationHandler.CompleteAsync"/>, <c>beforeSwitch</c>).
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="period">Період прогону.</param>
    /// <param name="guards">Перераховані аркуші за документами.</param>
    /// <param name="includeApproved">✎ B80 N5: явне перекриття адміністратора — затверджені аркуші скоупу не відмовляють.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>ECR-CALC-0409</c> <c>recalcStateChanged</c>: аркуш ЗАТВЕРДЖЕНО або період
    /// закрито під час прогону. Відмова відкочує перемикання; прогін стає <c>Failed</c>,
    /// попередній лишається актуальним, а повтор задачі (<c>JobRetryPolicy</c> повторює
    /// конфлікт стану) планує вже з новим станом — затверджений аркуш пропустить і перенесе.
    /// ✎ B79: аркуш, ПОДАНИЙ під час прогону, не відмовляє — подані перераховує кожен прогін; позначку він отримає,
    /// якщо числа змінилися (<see cref="ResultChangesAsync"/> читає стан уже під цим блокуванням).
    /// </exception>
    /// <remarks>
    /// ⛔ Аркуш — через спільне блокування <see cref="ISheetEditGate.EnterEditAsync"/>, яке
    /// подання бере винятково: подання, що йшло паралельно, до цього моменту вже
    /// зафіксоване (і ми його бачимо) або чекатиме нашого коміту (і тоді перевірить
    /// <c>IsStale</c> уже за новим прогоном). Блокування — за зростанням (документ, аркуш),
    /// як у <c>RecalculationService.EnterSheetsAsync</c>. Без воріт (пряме конструювання в
    /// тестах) — той самий стан читається без блокування.
    /// <para>
    /// ⚠ Період, рядка якого немає, не відмовляє — як у <see cref="RefusedPeriodsAsync"/>.
    /// </para>
    /// </remarks>
    private async Task GuardCompletionAsync(
        int projectId, int period, IReadOnlyList<SheetGuard> guards, bool includeApproved, CancellationToken ct)
    {
        // ⛔ B69: архівація могла початися, поки прогін рахував, — перемикання в архівованому проєкті не відбувається.
        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Status, p.IsArchiving })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (project is not null
            && RecalculationWritePolicy.CheckProject(project.Status, project.IsArchiving) is var projectDenial
            && projectDenial != RecalculationWriteDenial.None)
        {
            throw RecalculationWritePolicy.Reject(
                projectDenial, period, guards.Count > 0 ? (object?)guards[0].DocumentId : null);
        }

        // ⛔ X6-01: стан — ЕФЕКТИВНИЙ (F-08), а не збережений: прогін, що завершується між
        // `ComputedCloseAt` і найближчою годинною задачею станів, інакше перемикав актуальність
        // у вже закритому періоді без погодження S1.
        var state = (await EffectiveStatesAsync(
                    db.Periods.Where(p => p.ProjectId == projectId && p.PeriodKeyValue == period), ct)
                .ConfigureAwait(false))
            .Select(p => (Domain.Enums.PeriodState?)p.State)
            .FirstOrDefault();

        // ⛔ Приймальна 08.11 (i7): закритий місяць перемикається, лише коли ВСІ документи прогону мають діючий виняток
        // за нього (у скоуп їх допустив `RefusedPeriodsAsync`); документ без винятку — та сама відмова, що й доти.
        if (state == Domain.Enums.PeriodState.Closed && guards.Count > 0)
        {
            var reopened = await ReopenedPeriodsAsync([.. guards.Select(g => g.DocumentId)], period, ct).ConfigureAwait(false);
            state = Domain.Services.DocumentPeriodReopenRule.Apply(
                Domain.Enums.PeriodState.Closed, guards.All(g => reopened.Contains((g.DocumentId, period))));
        }

        if (state is { } current
            && RecalculationWritePolicy.Check(current, false) != RecalculationWriteDenial.None)
        {
            throw StateChanged(guards.Count > 0 ? guards[0].DocumentId : 0, period, sheetDefId: null);
        }

        // ⛔ X6-02: чекати дозволено лише перший аркуш — поки нічого не тримаємо. Далі без
        // черги: інакше, чекаючи аркуш N із S на попередніх, перемикання ставило б за собою
        // подання тих аркушів і автозбереження їхніх редакторів. Відмова (`ECR-DOC-4091`)
        // відкочує перемикання, як і `recalcStateChanged`; викликач повторює лише цю транзакцію,
        // а вичерпавши повтори — відкладає задачу (Y1-02).
        var holdsAny = false;

        foreach (var guard in guards.OrderBy(g => g.DocumentId))
        {
            foreach (var sheetDefId in guard.Sheets)
            {
                var status = sheetGate is not null
                    ? await (holdsAny
                            ? sheetGate.EnterEditNoWaitAsync(guard.DocumentId, sheetDefId, new PeriodKey(period), ct)
                            : sheetGate.EnterEditAsync(guard.DocumentId, sheetDefId, new PeriodKey(period), ct))
                        .ConfigureAwait(false)
                    : await db.ApprovalStates
                        .AsNoTracking()
                        .OriginalOnly()
                        .ExceptWorkflowNone(db) // ✎ B138: як SheetEditGate - версія без погодження = Draft
                        .Where(a => a.DocumentId == guard.DocumentId && a.SheetDefId == sheetDefId && a.PeriodKey == period)
                        .Select(a => (Domain.Enums.DocumentStatus?)a.Status)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false) ?? Domain.Enums.DocumentStatus.Draft;
                holdsAny = true;

                // ✎ B79 (слово людини 16.11): поданий аркуш перемикає будь-який прогін, і нічний теж; затверджений — ніколи,
                // ✎ B80 N5 (D-554): крім явного перекриття адміністратора (`IncludeApproved`) — аркуш у скоупі саме через нього.
                if (status is Domain.Enums.DocumentStatus.Approved && !includeApproved)
                {
                    throw StateChanged(guard.DocumentId, period, sheetDefId);
                }
            }
        }
    }

    /// <summary>
    /// ✎ B79: подані аркуші періоду, у яких новий прогін змінив результати методологій, — у <paramref name="changes"/>.
    /// Виконується в транзакції перемикання після <see cref="GuardCompletionAsync"/>, до переносу й перемикання.
    /// </summary>
    /// <param name="runId">Новий прогін періоду (ще не актуальний).</param>
    /// <param name="period">Період прогону.</param>
    /// <param name="guards">Аркуші, які прогін перерахував методологіями, за документами.</param>
    /// <param name="changes">Куди скласти змінені подані аркуші.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <para>
    /// ⚠ Чому тут, а не на шляху запису: прогін пише ВСІ результати заново під новим <c>CalculationRunId</c>, тож
    /// «записано» нічого не каже про зміну. Єдине місце, де старі й нові числа існують разом і старі ще актуальні, —
    /// ця транзакція (після коміту старі прибираються, Q-17). Порівняння — у сховищі
    /// (<see cref="RunCalculationHandler.ChangedResultsAsync"/>), одним запитом на документ, і лише за методологіями,
    /// які прогін перерахував (перенесені в новому прогоні ще відсутні й змінитися не могли).
    /// </para>
    /// <para>
    /// ⚠ Зміна відноситься до аркуша через екземпляр таблиці результату (D-401); результат без екземпляра — до всіх
    /// таблиць, до яких методологія прив'язана в цьому прогоні (той самий рівень, на якому задача вирішує «рахувати чи
    /// переносити»: результат лягає за методологією й рядком). Тобто в сумнівному випадку позначка ставиться.
    /// </para>
    /// <para>
    /// Запитів: один на стани (усі документи періоду) і, лише коли серед них є подані, один на екземпляри таблиць і
    /// по одному порівнянню на документ із поданим аркушем. Період без поданих аркушів коштує один короткий запит.
    /// Повтор транзакції (Y1-02) безпечний: набір ідемпотентний.
    /// </para>
    /// </remarks>
    private async Task ResultChangesAsync(
        long runId, int period, IReadOnlyList<SheetGuard> guards, SubmittedSheetChanges changes, CancellationToken ct)
    {
        var recalculated = guards.Where(g => g.Bindings.Count > 0).ToList();
        if (recalculated.Count == 0)
        {
            return;
        }

        var documentIds = recalculated.Select(g => g.DocumentId).Distinct().ToList();
        var submitted = (await db.ApprovalStates
                .AsNoTracking()
                .ExceptWorkflowNone(db) // ✎ B138: версія без погодження «поданих» не має — як у SheetLogAsync
                .Where(a => documentIds.Contains(a.DocumentId) && a.PeriodKey == period
                            && a.Status == Domain.Enums.DocumentStatus.Submitted)
                .Select(a => new { a.DocumentId, a.SheetDefId, a.SheetCopyId })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .Select(a => (a.DocumentId, a.SheetDefId, CopyKey: a.SheetCopyId ?? 0L))
            .ToHashSet();
        if (submitted.Count == 0)
        {
            return;
        }

        var submittedDocuments = submitted.Select(s => s.DocumentId).Distinct().ToList();
        var sheetOfInstance = (await db.TableInstances
                .AsNoTracking()
                .Where(i => submittedDocuments.Contains(i.DocumentId) && i.PeriodKeyValue == period)
                .Join(db.TableDefs, i => i.TableDefId, td => td.Id, (i, td) => new { i.Id, td.SheetDefId, i.SheetCopyId })
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToDictionary(x => x.Id, x => (x.SheetDefId, x.SheetCopyId));

        foreach (var guard in recalculated.Where(g => submittedDocuments.Contains(g.DocumentId)))
        {
            var changed = await runs
                .ChangedResultsAsync(runId, guard.DocumentId, [.. guard.Bindings.Select(b => b.MethodologyId).Distinct()], ct)
                .ConfigureAwait(false);

            foreach (var change in changed)
            {
                IEnumerable<long> tables = change.TableInstanceId is { } table
                    ? new[] { table }
                    : guard.Bindings.Where(b => b.MethodologyId == change.MethodologyId).Select(b => b.TableInstanceId);

                foreach (var tableInstanceId in tables)
                {
                    if (sheetOfInstance.TryGetValue(tableInstanceId, out var sheet)
                        && submitted.Contains((guard.DocumentId, sheet.SheetDefId, sheet.SheetCopyId ?? 0L)))
                    {
                        changes.Add(guard.DocumentId, period, sheet.SheetDefId, sheet.SheetCopyId);
                    }
                }
            }
        }
    }

    /// <summary>Відмова D2-03: стан аркуша чи періоду змінився під час перерахунку.</summary>
    private static ConcurrencyConflictException StateChanged(long documentId, int period, int? sheetDefId)
        => new(
            "ECR-CALC-0409",
            $"Стан аркуша чи періоду {period.ToString(CultureInfo.InvariantCulture)} документа "
            + $"{documentId.ToString(CultureInfo.InvariantCulture)} змінився під час перерахунку: результати не застосовано.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-0409.recalcStateChanged",
                ["documentId"] = documentId,
                ["periodKey"] = period,
                ["sheetDefId"] = sheetDefId,
            });

    /// <summary>
    /// Періоди документа, у які цей прогін писати НЕ МАЄ ПРАВА (ФВ-9.7, ФВ-9.17).
    /// </summary>
    /// <param name="request">Завдання; <c>PeriodKey = null</c> — увесь рік.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключі періодів, у які запис заборонений.</returns>
    /// <exception cref="Ecr.Application.Errors.BusinessRuleException">
    /// <c>ECR-CALC-4221</c> — завдання назвало КОНКРЕТНИЙ період, і писати в
    /// нього не можна.
    /// </exception>
    /// <remarks>
    /// ⛔ Дві різні поведінки, і різниця не косметична.
    /// <list type="bullet">
    /// <item>
    /// Період названий ЯВНО (кнопка «Перерахувати» на документі) — ВИНЯТОК.
    /// Тихо не зробити нічого й повернути «успішно» означало б показати
    /// людині «перераховано» там, де не перераховано нічого; цей самий клас
    /// мовчазної відмови в цьому файлі вже ловили (Q-331).
    /// </item>
    /// <item>
    /// Період не названий (<c>PeriodKey = null</c> — нічний розклад,
    /// перерахунок проєкту за весь рік) — ВИЛУЧЕННЯ зі скоупу. Запит не
    /// називав закритого періоду: він сказав «усе, що можна перерахувати».
    /// Відмовити цілим прогоном означало б зупинити нічний перерахунок
    /// назавжди, щойно закриється перший період року.
    /// </item>
    /// </list>
    ///
    /// ⚠ Правило — НЕ тут, а в <see cref="RecalculationWritePolicy"/>: тут
    /// лише збирання фактів у гранулярності задачі. Саме роздвоєння правила
    /// (одна копія в <c>RunCalculationHandler</c> і жодної на цьому шляху) і
    /// було дефектом.
    ///
    /// ⚠ Подані аркуші рахуються ЗА ДОКУМЕНТОМ, а не за проєктом, як у
    /// <c>IWorkflowStore.HasSubmittedSheetsAsync</c>: прогін тут звужений до
    /// одного документа, і блокувати його через поданий аркуш СУСІДНЬОГО
    /// документа того ж проєкту було б ширше за правило (ФВ-9.17 — про зріз,
    /// а зріз належить аркушу документа).
    /// </remarks>
    /// <param name="annualOnly">
    /// ✎ LAND-YEAR B24 (D-NEW-3): куди скласти закриті якорі річної розкладки у відкритому році (якір → місяці року) —
    /// вони НЕ відмовляють і не вилучаються, а рахуються лише не-місячними цілями під агрегатом року
    /// (<see cref="AnnualOnlyAnchorsAsync"/>); <c>null</c> — не збирати (рішення те саме).
    /// </param>
    private async Task<IReadOnlySet<int>> RefusedPeriodsAsync(
        RecalculationRequest request, CancellationToken ct, Dictionary<int, IReadOnlyList<int>>? annualOnly = null)
    {
        // ⛔ B69: архівований (чи той, що архівується) проєкт — відмова цілим прогоном, і роком, і названим періодом. Нижче
        // виняток «місяць відкрито для документа» піднімає закритий місяць до Grace; виняток, що пережив архівацію,
        // інакше пускав прогін писати в архівований проєкт.
        var archived = await db.Documents
            .AsNoTracking()
            .Where(d => d.Id == request.DocumentId)
            .Join(db.Projects.AsNoTracking(), d => d.ProjectId, p => p.Id, (d, p) => new { p.Status, p.IsArchiving })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (archived is not null
            && RecalculationWritePolicy.CheckProject(archived.Status, archived.IsArchiving) is var projectDenial
            && projectDenial != RecalculationWriteDenial.None)
        {
            throw RecalculationWritePolicy.Reject(projectDenial, request.PeriodKey ?? 0, request.DocumentId);
        }

        // ⛔ X6-01: ефективний стан (F-08) — те саме правило, що й у `GuardCompletionAsync`
        // і в рішенні про запис; інакше прогін, допущений тут за збереженим `Grace`, падав би
        // на перемиканні за ефективним `Closed` і повторювався до годинної задачі станів.
        // ✎ LAND-YEAR B59: рік кнопки річного документа звужує й стани (решта років у скоуп не потрапляє).
        var fromKey = request.FromKey;
        var toKey = request.ToKey;
        var states = await EffectiveStatesAsync(
                db.Periods.Where(p => db.Documents.Any(d => d.Id == request.DocumentId && d.ProjectId == p.ProjectId)
                                      && (request.PeriodKey == null || p.PeriodKeyValue == request.PeriodKey)
                                      && p.PeriodKeyValue >= fromKey && p.PeriodKeyValue <= toKey),
                ct)
            .ConfigureAwait(false);

        // ⛔ Приймальна 08.11 (i7): місяць, відкритий лише для ЦЬОГО документа, у скоупі його перерахунку (як Grace);
        // решта документів того самого місяця винятку не мають і пропускають його, як і раніше.
        // Запит — лише коли є закритий місяць: інакше піднімати нічого.
        if (states.Exists(s => s.State == Domain.Enums.PeriodState.Closed))
        {
            var reopened = await ReopenedPeriodsAsync([request.DocumentId], request.PeriodKey, ct).ConfigureAwait(false);
            states = [.. states.Select(s => s with
            {
                State = Domain.Services.DocumentPeriodReopenRule.Apply(
                    s.State, reopened.Contains((request.DocumentId, s.PeriodKeyValue))),
            })];
        }

        // ⛔ RC15 (P2-A): для області аркуша (`SheetDefId`) період відхиляється, коли поданий САМ цей аркуш; для
        // УСЬОГО документа — лише коли ВСІ аркуші, що мають таблиці в періоді, пропущені (рахувати
        // нічого). Частково пропущені періоди лишаються в скоупі: такі аркуші пропускає `ApprovedSheetsAsync`.
        // ✎ B79: «пропущений» — лише ЗАТВЕРДЖЕНИЙ; період, де все подано, рахується (і нічним розкладом теж).
        // ✎ F13 p218 (P2, правило людини 07.11 «пропускати лише погоджені, решту перераховувати; подані — з позначкою»):
        // «рахувати нічого» — це КОЖЕН екземплярний скоуп (аркуш × копія, RS) періоду пропущений, а не лише оригінал.
        // Доти стан брався з оригіналу (`OriginalOnly`): затверджений оригінал відмовляв періодом (4221) і чернетці-копії
        // того самого аркуша (SHEETNAME після перейменування, ручний «Перерахувати» документа з копіями). Пропуск
        // затверджених — по аркушу у фазі формул (`RecalculationService.EnterSheetsAsync`) і методологій (`IsActive`).
        var skippedScopes = request.IncludeApproved
            ? new HashSet<(int PeriodKey, int SheetDefId, long CopyKey)>()
            : await SkippedScopesAsync(request, ct).ConfigureAwait(false);
        var candidatePeriods = skippedScopes.Select(s => s.PeriodKey).Distinct().ToList();
        HashSet<int> submittedKeys = [];

        if (candidatePeriods.Count > 0)
        {
            var instanceScopes = await db.TableInstances
                .AsNoTracking()
                .Where(i => i.DocumentId == request.DocumentId && candidatePeriods.Contains(i.PeriodKeyValue))
                .Join(db.TableDefs, i => i.TableDefId, td => td.Id, (i, td) => new { i.PeriodKeyValue, td.SheetDefId, i.SheetCopyId })
                .Where(x => request.SheetDefId == null || x.SheetDefId == request.SheetDefId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            submittedKeys = [.. candidatePeriods.Where(period =>
            {
                var scopes = instanceScopes
                    .Where(x => x.PeriodKeyValue == period)
                    .Select(x => (x.SheetDefId, CopyKey: x.SheetCopyId ?? 0))
                    .Distinct()
                    .ToList();
                return scopes.Count > 0 && scopes.All(s => skippedScopes.Contains((period, s.SheetDefId, s.CopyKey)));
            })];
        }

        var refused = new HashSet<int>();

        // ✎ LAND-YEAR B24: лише коли серед станів є закритий місяць (інакше — жодного нового запиту).
        var anchors = states.Exists(s => s.State == Domain.Enums.PeriodState.Closed)
            ? await AnnualOnlyAnchorsAsync(request, states, ct).ConfigureAwait(false)
            : new Dictionary<int, IReadOnlyList<int>>();

        foreach (var period in states)
        {
            if (anchors.TryGetValue(period.PeriodKeyValue, out var yearKeys))
            {
                annualOnly?.TryAdd(period.PeriodKeyValue, yearKeys);
                continue;
            }

            var denial = RecalculationWritePolicy.Check(
                period.State, submittedKeys.Contains(period.PeriodKeyValue));

            if (denial == RecalculationWriteDenial.None)
            {
                continue;
            }

            refused.Add(period.PeriodKeyValue);

            if (request.PeriodKey == period.PeriodKeyValue)
            {
                throw RecalculationWritePolicy.Reject(denial, period.PeriodKeyValue, request.DocumentId);
            }
        }

        // ⚠ Період, рядка якого в `doc.Period` немає, у відмову НЕ потрапляє:
        // «стану не знайшли» — це не «стан заборонний». Такий випадок падає
        // нижче своїм власним повідомленням (`RecalculationService.PeriodOf`,
        // `ECR-PRD-0404`), і підміняти його чужим кодом означало б сховати
        // справжню причину за правдоподібною.
        return refused;
    }

    /// <summary>
    /// ✎ LAND-YEAR B24 (L1b, D-NEW-3 «так»): закриті місяці документа, які є ЯКОРЕМ року річної розкладки
    /// (<c>YearWithMonthColumns</c>) у ще не закритому році (<see cref="Ecr.Application.Security.AnnualPeriodRule.AnchorWritesAnnualTargets"/>), —
    /// якір → ключі всіх місяців календаря року (для агрегатного стану аркуша під час запису).
    /// </summary>
    /// <param name="request">Завдання (документ).</param>
    /// <param name="states">Ефективні стани періодів скоупу (з винятком документа).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <para>
    /// Не-місячні колонки маршрутизованих таблиць належать якорю, але їхній «річний контекст» — агрегат місяців
    /// (<see cref="Ecr.Application.Security.AnnualPeriodRule"/>, те саме правило, що в PATCH і доступі). Доти закритий січень відмовляв увесь
    /// перерахунок якоря, і річні підсумки (не-місячні формули над C_Mm) лишались застарілими до кінця року.
    /// </para>
    /// <para>
    /// ⛔ Лише річна розкладка: версії <c>PerMonthCopies</c> (зокрема Land 1.0.16.0) — відмова закритим місяцем, як доти.
    /// Запити — лише коли серед станів є закритий місяць: 1 (проєкт документа) + на рік: календар, розкладка версії
    /// якоря (лише коли якір закритий) і ≤ 1 винятки документа.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<int, IReadOnlyList<int>>> AnnualOnlyAnchorsAsync(
        RecalculationRequest request, List<PeriodStateRow> states, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyList<int>>();
        var closed = states
            .Where(s => s.State == Domain.Enums.PeriodState.Closed)
            .Select(s => s.PeriodKeyValue)
            .ToHashSet();
        if (closed.Count == 0)
        {
            return result;
        }

        var document = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == request.DocumentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                select new { ProjectId = p.Id, p.PeriodKind })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (document is null || document.PeriodKind != Domain.Enums.PeriodKind.Monthly)
        {
            return result;
        }

        HashSet<(long DocumentId, int PeriodKey)>? reopened = null;
        foreach (var year in closed.Select(k => k / 100).Distinct().Order())
        {
            var yearStates = await EffectiveStatesAsync(PeriodCalendar.YearPeriods(db, document.ProjectId, year), ct)
                .ConfigureAwait(false);
            if (PeriodCalendar.AnchorOf(yearStates.Select(s => s.PeriodKeyValue)) is not { } anchor
                || !closed.Contains(anchor))
            {
                continue;
            }

            // Розкладка версії ПЕРІОДУ-якоря (шкала версій проєкту, doc.fn_VersionOf), а не «чинної» версії проєкту.
            var layout = await db.TemplateVersions
                .AsNoTracking()
                .Where(v => v.Id == EcrDbContext.VersionOf(document.ProjectId, anchor))
                .Select(v => (Domain.Enums.TemplateLayout?)v.Layout)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (layout != Domain.Enums.TemplateLayout.YearWithMonthColumns)
            {
                continue;
            }

            reopened ??= await ReopenedPeriodsAsync([request.DocumentId], null, ct).ConfigureAwait(false);
            var effective = yearStates
                .Select(s => Domain.Services.DocumentPeriodReopenRule.Apply(
                    s.State, reopened.Contains((request.DocumentId, s.PeriodKeyValue))))
                .ToList();

            if (Ecr.Application.Security.AnnualPeriodRule.AnchorWritesAnnualTargets(Domain.Enums.PeriodState.Closed, effective))
            {
                result[anchor] = [.. yearStates.Select(s => s.PeriodKeyValue).Order()];
            }
        }

        return result;
    }

    /// <summary>
    /// Екземплярні скоупи (період, аркуш, копія; <c>0</c> — оригінал), які прогін ПРОПУСКАЄ: лише затверджені.
    /// Те саме правило, що <see cref="ApprovedSheetsAsync"/>, але з копіями (RS): стан копії свій.
    /// </summary>
    /// <remarks>
    /// ⛔ B79 (слово людини 16.11: «якщо документ поданий або інші статуси окрім погоджений ми маємо перераховувати його
    /// якщо вхідні дані для нього змінилися»): поданий аркуш перераховує КОЖЕН прогін — кнопка, автоперерахунок після
    /// збору й залежних і нічний розклад. Доти нічний розклад (<c>TriggeredByUserId = null</c>, без
    /// <c>RecalculateSubmitted</c>) подані мовчки пропускав, бо інакше кожна ніч позначала б усі подані аркуші; тепер
    /// позначку ставить не факт прогону, а зміна чисел (<see cref="SubmittedSheetChanges"/>), тож причина пропуску зникла.
    /// </remarks>
    private async Task<HashSet<(int PeriodKey, int SheetDefId, long CopyKey)>> SkippedScopesAsync(
        RecalculationRequest request, CancellationToken ct)
    {
        var fromKey = request.FromKey;
        var toKey = request.ToKey;
        // ✎ B138: версія без погодження нічого не пропускає (старі стани після переносу версії не діють).
        var rows = await db.ApprovalStates
            .AsNoTracking()
            .ExceptWorkflowNone(db)
            .Where(s => s.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || s.PeriodKey == request.PeriodKey)
                        && s.PeriodKey >= fromKey && s.PeriodKey <= toKey
                        && (request.SheetDefId == null || s.SheetDefId == request.SheetDefId)
                        && s.Status == Domain.Enums.DocumentStatus.Approved)
            .Select(s => new { s.PeriodKey, s.SheetDefId, s.SheetCopyId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.PeriodKey, r.SheetDefId, r.SheetCopyId ?? 0L))];
    }

    /// <summary>Аркуші документа за періодами, які прогін ПРОПУСКАЄ: лише затверджені (ФВ-9.17, ✎ B79).</summary>
    /// <param name="request">Завдання.</param>
    /// <param name="periodKey">Період; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ключ періоду → аркуші, що там затверджені.</returns>
    private async Task<Dictionary<int, HashSet<int>>> ApprovedSheetsAsync(
        RecalculationRequest request, int? periodKey, CancellationToken ct)
    {
        // RS: стан копії не робить затвердженим аркуш-оригінал; копії мають власні ворота у фазі формул (EnterSheetsAsync).
        // ✎ B79: поданий аркуш перераховує будь-який прогін (кнопка, тригери, нічний розклад); пропуск — лише затверджені.
        var rows = await db.ApprovalStates
            .AsNoTracking()
            .OriginalOnly()
            .ExceptWorkflowNone(db) // ✎ B138: версія без погодження нічого не пропускає
            .Where(s => s.DocumentId == request.DocumentId
                        && (periodKey == null || s.PeriodKey == periodKey)
                        && s.Status == Domain.Enums.DocumentStatus.Approved)
            .Select(s => new { s.PeriodKey, s.SheetDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows
            .GroupBy(row => row.PeriodKey)
            .ToDictionary(group => group.Key, group => group.Select(row => row.SheetDefId).ToHashSet());
    }

    /// <summary>
    /// Скільки перерахунок чекає на запис даних довідника, який читають методології, перш ніж
    /// відкластися (Z5-01), мс. Менше за <see cref="LockWaitGuard.LockTimeoutMs"/>: задача тримає місце
    /// лейна й applock документа, а відкладення їх звільняє.
    /// </summary>
    internal const int RegistryWriterWaitMs = 5_000;

    /// <summary>Ресурс у <see cref="JobDeferredException"/> відкладення через запис довідника (Z5-01).</summary>
    internal const string RegistryWriterResource = "cfg.RegistryDef";

    private static readonly string SetRegistryWriterWait =
        string.Create(CultureInfo.InvariantCulture, $"SET LOCK_TIMEOUT {RegistryWriterWaitMs};");

    private const string ResetRegistryWriterWait = "SET LOCK_TIMEOUT -1;";

    /// <summary>Ресурс <c>sp_getapplock</c> для перерахунку документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <returns>Ім'я ресурсу.</returns>
    public static string DocumentLockResource(long documentId) => RecalculationDocumentLock.Resource(documentId);

    /// <summary>
    /// Чекає комітів записів даних довідників, що вже почалися, перш ніж прогін візьме <c>startedAt</c>
    /// (X3-03 / D1-03).
    /// </summary>
    /// <remarks>
    /// ⛔ Знімок довідника прогону — <c>AS OF RegistryAsOfUtc</c> (від <c>startedAt</c>), а <c>PeriodStart</c>
    /// рядка темпоральної таблиці — ПОЧАТОК транзакції запису. Запис, що почався до старту прогону й
    /// комітиться після читання знімка, у знімок не потрапляв, а його <c>DataChangedAt &lt; StartedAt</c>
    /// гасив позначку «застаріло» (<c>StaleResultsQuery</c>, <c>RegistryImpactStore</c>): число на старому
    /// довіднику жило до випадкового перерахунку. Кожен запис даних довідника першим оператором своєї
    /// транзакції бере X-лок рядка <c>cfg.RegistryDef</c> (<c>UnitOfWork.ApplyRegistryRevisionBumpsAsync</c>),
    /// тож S-читання <c>DataRevision</c> під <c>READCOMMITTEDLOCK</c> (а не версії RCSI) чекає саме їх.
    /// <c>MAX(DataRevision)</c>, а не <c>COUNT(*)</c>: лічильник оптимізатор узяв би з некластерного
    /// індексу, якого оновлення ревізії не блокує.
    /// <para>
    /// ⚠ Залишок вікна — мілісекунди між міткою <c>DataChangedAt</c> і першим <c>UPDATE</c> писача. Якщо
    /// писач готується в зовнішній транзакції ДО першого збереження (rescan синку, <c>beforeSave</c>
    /// імпорту), його мітка <c>DataChangedAt</c> ставиться при збереженні, тобто ПІСЛЯ <c>startedAt</c>, і
    /// позначка «застаріло» спрацьовує. Лок береться поза транзакцією й звільняється одразу; задача тримає
    /// лише сесійний applock документа, якого писачі довідника не беруть. Не SQL Server (тести на інших
    /// провайдерах) — без очікування.
    /// </para>
    /// <para>
    /// ⛔ Z5-01 (аудит R8). Раніше читалася ВСЯ <c>cfg.RegistryDef</c> без межі очікування: скан брав S на
    /// кожному рядку, тож перерахунок будь-якого проєкту чекав на запис БУДЬ-ЯКОГО довідника (імпорт CSV з
    /// правилами, синк із rescan у тій самій транзакції) до <c>CommandTimeout</c> (60 с), тримаючи місце
    /// лейна й applock документа, а далі — повтори <c>JobRetryPolicy</c>. Тепер:
    /// (1) лише довідники з ребром «формула версії методології» (<c>RegistryUse.SourceKind = 1</c>) — рівно
    /// той набір, для якого існує позначка «застаріло» (<c>StaleResultsQuery</c>, <c>RegistryImpactStore</c>
    /// фільтрують за тим самим видом ребра); запис довідника без такого ребра результатів методологій не
    /// гасить і чекати його нема чого; рядки — пошуком по PK (<c>FORCESEEK</c>), без скану чужих;
    /// (2) межа очікування — <see cref="RegistryWriterWaitMs"/> (<c>SET LOCK_TIMEOUT</c>); вичерпана —
    /// <see cref="JobDeferredException"/>, як зайнятий документ: місце воркера й applock звільняються,
    /// повтор — через <see cref="RecalculationDocumentLock.DeferDelay"/> під стелею
    /// <see cref="JobDeferral.MaxDeferral"/>. Прогону на знімку без незакоміченого запису з погашеною
    /// позначкою, як і раніше, не буває.
    /// </para>
    /// <para>
    /// ⚠ <c>SET LOCK_TIMEOUT</c> — налаштування сеансу: <c>SET</c> і читання йдуть одним відкритим
    /// з'єднанням контексту, після читання ліміт знімається (з'єднання, відкрите тут, закривається — пул
    /// скидає сеанс; уже відкрите — <c>SET LOCK_TIMEOUT -1</c>), щоб решта операторів задачі не отримала
    /// 1222.
    /// </para>
    /// </remarks>
    private async Task AwaitRegistryWritersAsync(CancellationToken ct)
    {
        if (!db.Database.IsSqlServer())
        {
            return;
        }

        var openedHere = db.Database.GetDbConnection().State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await db.Database
                .ExecuteSqlRawAsync(SetRegistryWriterWait, ct)
                .ConfigureAwait(false);

            _ = await db.Database
                .SqlQuery<int>($"""
                    SELECT ISNULL(MAX(rd.DataRevision), 0) AS Value
                    FROM cfg.RegistryUse AS u
                    JOIN cfg.RegistryDef AS rd WITH (READCOMMITTEDLOCK, FORCESEEK) ON rd.Id = u.RegistryDefId
                    WHERE u.SourceKind = {Domain.Entities.Configuration.RegistryUse.MethodologyVersionSource}
                    """)

                // `MAX` повертає рівно один рядок; `OrderBy` + `Take` — межа архітектурного правила 6 і EF 10102,
                // як у `PartitionCheckJob`.
                .OrderBy(v => v)
                .Take(1)
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (LockWaitGuard.IsLockWaitTimeout(ex))
        {
            throw new JobDeferredException(
                RecalculationDocumentLock.DeferDelay,
                "Registry data write is still in progress; recalculation deferred.",
                RegistryWriterResource);
        }
        finally
        {
            if (openedHere)
            {
                await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
            else
            {
                await db.Database
                    .ExecuteSqlRawAsync(ResetRegistryWriterWait, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Бере ексклюзивний лок документа на весь час задачі.</summary>
    /// <returns><c>null</c> — лок не потрібен (перерахунок проєкту без планувальника, не SQL Server).</returns>
    /// <remarks>
    /// ⛔ O1 (I2 ФВ-9.8): очікування — лише <see cref="RecalculationDocumentLock.BusyWait"/>,
    /// а не 15 хв. Документ рахує інша задача — <see cref="JobDeferredException"/>:
    /// виконавець повертає задачу в чергу, звільнивши слот, без спроби ретраю.
    /// </remarks>
    private Task<SqlDistributedLock?> AcquireDocumentLockAsync(long documentId, CancellationToken ct)
        => RecalculationDocumentLock.AcquireAsync(db, documentId, RecalculationDocumentLock.BusyWait, ct);

    /// <summary>Усі документи проєкту — для перерахунку «на весь проєкт».</summary>
    /// <remarks>
    /// Q-151/Q-162: саме цей перелік замінює «нуль документів» на «усі
    /// документи проєкту», коли <c>DocumentId</c> у завданні — 0 або менше.
    /// </remarks>
    private async Task<IReadOnlyList<long>> ProjectDocumentIdsAsync(int projectId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Загортає повідомлення в конверт «Документ N (i з M): {message}»
    /// (<c>jobs.documentPrefix</c>), коли документів кілька — той самий умовний
    /// префікс, що діяв тут ДО <c>Q-326</c>, лише тепер структурований.
    /// </summary>
    /// <param name="inner">Повідомлення нижчого шару.</param>
    /// <param name="documentId">Документ поточної ітерації.</param>
    /// <param name="index">Порядковий номер документа, від 1.</param>
    /// <param name="count">Скільки всього документів у прогоні.</param>
    private static JobProgressMessageEnvelope WithDocumentPrefix(
        JobProgressMessageEnvelope inner, long documentId, int index, int count)
        => count > 1
            ? new JobProgressMessageEnvelope(
                "jobs.documentPrefix",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["id"] = documentId.ToString(CultureInfo.InvariantCulture),
                    ["index"] = index.ToString(CultureInfo.InvariantCulture),
                    ["count"] = count.ToString(CultureInfo.InvariantCulture),
                },
                inner)
            : inner;

    /// <summary>Скільки номерів рядків без правила називає повідомлення.</summary>
    private const int NoMatchingRuleRowsShown = 10;

    /// <summary>
    /// Конверт «No matching rule … row N» (<c>jobs.recalcNoMatchingRule</c>): кількість рядків
    /// і перші номери. Ключів рядків і значень комірок тут немає навмисно.
    /// </summary>
    /// <param name="unmatched">Рядки без правила за весь прогін.</param>
    private static JobProgressMessageEnvelope NoMatchingRuleEnvelope(List<UnmatchedRow> unmatched)
    {
        var shown = string.Join(
            ", ",
            unmatched.Take(NoMatchingRuleRowsShown).Select(r => r.RowNumber.ToString(CultureInfo.InvariantCulture)));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["count"] = unmatched.Count.ToString(CultureInfo.InvariantCulture),
            ["rows"] = unmatched.Count > NoMatchingRuleRowsShown ? shown + ", …" : shown,
        };

        // AN-69: понад стелю на таблицю в `calc.CalculationIssue` потрапляє не все - повідомлення це каже.
        var truncated = TruncatedIssues(unmatched);
        if (truncated > 0)
        {
            parameters["truncated"] = truncated.ToString(CultureInfo.InvariantCulture);
        }

        return new JobProgressMessageEnvelope("jobs.recalcNoMatchingRule", parameters);
    }

    /// <summary>Скільки рядків без правила не збережено в <c>calc.CalculationIssue</c> через стелю на таблицю (AN-69).</summary>
    /// <param name="unmatched">Рядки без правила за весь прогін.</param>
    internal static int TruncatedIssues(IEnumerable<UnmatchedRow> unmatched)
        => unmatched
            .GroupBy(r => r.TableInstanceId)
            .Sum(g => Math.Max(0, g.Count() - Application.Ports.ICalculationIssueStore.MaxPerTable));

    /// <summary>
    /// Конверт «правило категорії відхилило рядки» (<c>jobs.recalcCategoryRuleRejected</c>, L2-6):
    /// кількість і перші номери. Ключів рядків, тексту правила й значень комірок тут немає навмисно.
    /// </summary>
    /// <param name="rejected">Відхилені рядки за весь прогін.</param>
    private static JobProgressMessageEnvelope CategoryRuleRejectedEnvelope(List<RejectedRow> rejected)
    {
        var shown = string.Join(
            ", ",
            rejected.Take(NoMatchingRuleRowsShown).Select(r => r.RowNumber.ToString(CultureInfo.InvariantCulture)));

        return new JobProgressMessageEnvelope(
            "jobs.recalcCategoryRuleRejected",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = rejected.Count.ToString(CultureInfo.InvariantCulture),
                ["rows"] = rejected.Count > NoMatchingRuleRowsShown ? shown + ", …" : shown,
            });
    }

    /// <summary>
    /// Конверт «рядкам бракує входів формул» (<c>jobs.recalcMissingInputs</c>, AN-180): кількість і перші номери.
    /// Ключів рядків, назв колонок і значень комірок тут немає навмисно — колонки називає банер над сіткою.
    /// </summary>
    /// <param name="rows">Рядки без значення через відсутні входи за весь прогін.</param>
    private static JobProgressMessageEnvelope MissingInputsEnvelope(List<RejectedRow> rows)
    {
        var shown = string.Join(
            ", ",
            rows.Take(NoMatchingRuleRowsShown).Select(r => r.RowNumber.ToString(CultureInfo.InvariantCulture)));

        return new JobProgressMessageEnvelope(
            "jobs.recalcMissingInputs",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = rows.Count.ToString(CultureInfo.InvariantCulture),
                ["rows"] = rows.Count > NoMatchingRuleRowsShown ? shown + ", …" : shown,
            });
    }

    /// <summary>
    /// Конверт «привʼязані методології без опублікованих версій пропущено» (<c>jobs.recalcMethodologyUnpublished</c>,
    /// G14): кількість і перші ідентифікатори методологій.
    /// </summary>
    /// <param name="methodologyIds">Пропущені методології за весь прогін.</param>
    private static JobProgressMessageEnvelope UnpublishedMethodologyEnvelope(SortedSet<int> methodologyIds)
    {
        var shown = string.Join(
            ", ",
            methodologyIds.Take(NoMatchingRuleRowsShown).Select(id => id.ToString(CultureInfo.InvariantCulture)));

        return new JobProgressMessageEnvelope(
            "jobs.recalcMethodologyUnpublished",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = methodologyIds.Count.ToString(CultureInfo.InvariantCulture),
                ["methodologies"] = methodologyIds.Count > NoMatchingRuleRowsShown ? shown + ", …" : shown,
            });
    }

    /// <summary>Розбирає завдання черги.</summary>
    /// <remarks>
    /// Payload приходить як анонімний об'єкт від use-case і як JSON із черги —
    /// обидва шляхи мусять читатися однаково, інакше задача працювала б
    /// у тесті й падала в проді.
    /// </remarks>
    private static RecalculationRequest Parse(object? payload)
    {
        if (payload is RecalculationRequest typed)
        {
            return typed;
        }

        var json = payload as string ?? JsonSerializer.Serialize(payload);

        return JsonSerializer.Deserialize<RecalculationRequest>(json, PayloadOptions)
               ?? throw new InvalidOperationException(
                   "Завдання перерахунку не розбирається: невідома форма payload.");
    }

    /// <summary>
    /// ✎ PERF-05 (огляд M3): чи задача — перерахунок, який натиснула людина (є автор, не дитина розкладу, не прохід читача),
    /// — той самий поділ, що прохід «спершу ручні» черги в базі (<c>DbJobQueue.ManualFilter</c>).
    /// </summary>
    /// <param name="jobType">Тип задачі.</param>
    /// <param name="payload">Тіло задачі (JSON).</param>
    internal static bool IsManualPress(Type jobType, string? payload)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        if (!typeof(IRecalculationJob).IsAssignableFrom(jobType) || string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions) is
                { TriggeredByUserId: not null, FanOutParentJobId: null or "", ForeignOwnerPass: false };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Чи задача — ДИТИНА розкладу перерахунку проєкту (<see cref="FanOutAsync"/>): документна задача з позначкою
    /// батька. Лише такі стають під межу <see cref="QuartzJobTypeLimiter.RecalcFanOutMaxConcurrency"/> (F1, WW-02).
    /// </summary>
    /// <param name="jobType">Тип реалізації задачі.</param>
    /// <param name="payload">Тіло задачі (JSON черги).</param>
    /// <returns><c>true</c> — дитина розкладу.</returns>
    /// <remarks>
    /// ⚠ Розбір толерантний: тіло, яке не розбирається, — не дитина (задача піде без межі й сама відмовить у
    /// <see cref="ExecuteAsync"/> зі своїм повідомленням), а не виняток у адаптері до старту задачі.
    /// </remarks>
    internal static bool IsFanOutChild(Type jobType, string? payload)
    {
        ArgumentNullException.ThrowIfNull(jobType);

        if (!typeof(IRecalculationJob).IsAssignableFrom(jobType) || string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            return JsonSerializer.Deserialize<RecalculationRequest>(payload, PayloadOptions) is
                { DocumentId: > 0, FanOutParentJobId: { Length: > 0 } };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Екземпляр таблиці документа разом із періодом, якому він належить.</summary>
    /// <remarks>
    /// ⚠ <paramref name="PeriodKeyValue"/> доданий не для зручності: без нього
    /// прив'язки не можна розкласти за періодами, а саме плаский набір
    /// прив'язок з одним спільним ключем періоду й був дефектом річного прогону.
    /// </remarks>
    /// <param name="Id">Ідентифікатор екземпляра.</param>
    /// <param name="TableDefId">Опис таблиці.</param>
    /// <param name="PeriodKeyValue">Період екземпляра.</param>
    /// <param name="SheetDefId">Аркуш, якому належить таблиця екземпляра.</param>
    private sealed record InstanceRow(long Id, int TableDefId, int PeriodKeyValue, int SheetDefId);

    /// <summary>Прив'язка методології до опису таблиці.</summary>
    private sealed record BindingRow(int TableDefId, int MethodologyId);

    /// <summary>Прив'язка разом із періодом, у якому вона рахується.</summary>
    private sealed record PeriodBindingRow(int PeriodKeyValue, CalculationBindingRef Binding);

    /// <summary>Стан одного періоду — для гейту запису.</summary>
    private sealed record PeriodStateRow(int PeriodKeyValue, Domain.Enums.PeriodState State);

    /// <summary>
    /// Стеля вибірки періодів для гейту стану: проєкт — один рік, тобто щонайбільше 12 періодів
    /// (D-108), як і <c>PeriodStore.MaxPeriods</c>; межа — правило 6 <c>LayerRulesTests</c>.
    /// </summary>
    private const int MaxPeriods = 64;

    /// <summary>Правило ефективного стану — те саме, що в рішенні про запис (F-08).</summary>
    private static readonly Domain.Services.PeriodStateCalculator PeriodStates = new();

    /// <summary>
    /// X6-01: ЕФЕКТИВНИЙ стан періодів на <c>clock.UtcNow</c> — тим самим правилом, що й
    /// <c>AccessDecisionService</c> (F-08) і <c>IPeriodStore.FindPeriodStateAsync</c>.
    /// </summary>
    /// <param name="source">Періоди, стан яких потрібен.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Лише для АКТИВНОГО проєкту (`A7-25`): періоди чернетки за датами не просуваються.
    /// Ефективний стан — лише вперед від збереженого, тож закритого він не відкриває.
    /// </remarks>
    private async Task<List<PeriodStateRow>> EffectiveStatesAsync(
        IQueryable<Domain.Entities.Documents.Period> source, CancellationToken ct)
    {
        var rows = await (
                from p in source.AsNoTracking()
                join project in db.Projects.AsNoTracking() on p.ProjectId equals project.Id
                orderby p.PeriodKeyValue
                select new { Period = p, project.Status, project.PeriodEnd, project.YearGraceOffsetDays, project.TimeZoneId })
            .Take(MaxPeriods)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        return [.. rows.Select(r => new PeriodStateRow(
            r.Period.PeriodKeyValue,
            r.Status == Domain.Enums.ProjectStatus.Active
                ? PeriodStates.Effective(
                    r.Period,
                    now,
                    Domain.Services.YearGraceWindow.For(
                        r.PeriodEnd, r.YearGraceOffsetDays, SiteTimeZone.Create(r.TimeZoneId).ToTimeZoneInfo()))
                : r.Period.State))];
    }

    /// <summary>
    /// Приймальна 08.11 (i): пари (документ, місяць) з ДІЮЧИМ на <c>clock.UtcNow</c> винятком «місяць відкрито лише для
    /// документа» (<c>doc.DocumentPeriodReopen</c>).
    /// </summary>
    /// <param name="documentIds">Документи.</param>
    /// <param name="periodKey">Місяць; <c>null</c> — усі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<HashSet<(long DocumentId, int PeriodKey)>> ReopenedPeriodsAsync(
        IReadOnlyCollection<long> documentIds, int? periodKey, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.DocumentPeriodReopens
            .AsNoTracking()
            .Where(x => documentIds.Contains(x.DocumentId)
                        && (periodKey == null || x.PeriodKeyValue == periodKey)
                        && x.ClosedAt == null
                        && x.OpenUntil > now)
            .OrderBy(x => x.DocumentId)
            .ThenBy(x => x.PeriodKeyValue)
            .Select(x => new { x.DocumentId, x.PeriodKeyValue })
            .Take(MaxPeriods * Math.Max(1, documentIds.Count))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.DocumentId, r.PeriodKeyValue))];
    }

    /// <summary>
    /// ✎ L1b / D5: прибирання прогонів, які задача зняла з актуальності, — ПІСЛЯ лока документа (оголошується раніше за
    /// нього, тож <c>await using</c> звільняє його пізніше).
    /// </summary>
    /// <remarks>
    /// Задача воркера черги в базі — доручення воркеру після фіксації рядка (<see cref="IJobLeaseContext.TryRunAfterSettle"/>):
    /// до неї рядок <c>Running</c>, і claim не бере інших задач документа (PSS-04). Інакше (Quartz, без оренди) — тут же,
    /// одразу після лока; скасована задача не прибирає (лишає нічному проходу). Не кидає: збій — журнал, як і доти
    /// (<see cref="RunCalculationHandler.PurgeSupersededAsync"/>), і виняток задачі не підміняється.
    /// </remarks>
    private sealed class PurgeAfterDocumentLock(
        RunCalculationHandler runs,
        IJobLeaseContext? lease,
        List<SupersededRun> superseded,
        ILogger<RecalculationJob>? logger,
        CancellationToken ct) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (superseded.Count == 0)
            {
                return;
            }

            IReadOnlyList<SupersededRun> handed = [.. superseded];
            superseded.Clear();
            if (lease?.TryRunAfterSettle(token => runs.PurgeSupersededAsync(handed, token)) == true)
            {
                return;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await runs.PurgeSupersededAsync(handed, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Прибирання після лока — не робота задачі: збій лише в журнал, решту доїсть нічний прохід.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPurgeAfterLockFailed(logger ?? NullLogger<RecalculationJob>.Instance, handed.Count, ex);
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "RecalculationJob: прибирання {Runs} прогон(ів), знятих з актуальності, після лока документа не вдалося; "
                  + "решту доїсть SupersededResultsSweepJob.")]
    private static partial void LogPurgeAfterLockFailed(ILogger logger, int runs, Exception exception);

    /// <summary>
    /// ✎ PSS-04: прогрес кроку ланцюжка — його 0…100 у частці всього ланцюжка: крок <c>Done + 1</c> із
    /// <see cref="Total"/>, тож шкала рядка задачі не стрибає назад на кожному кроці.
    /// </summary>
    /// <remarks>
    /// ⛔ L1b / D3 (стенд Land 21.11): доти <see cref="Total"/> починався з <c>Done + 1</c> і ставав справжнім лише після
    /// перечитування скоупу — перші ~4 с кожного кроку рядок показував «Період k з k», а відсоток стрибав (42 → 75 → 48).
    /// Тепер загальна кількість іде з payload кроку (<see cref="RecalculationRequest.ChainTotal"/>, її кладе попередній
    /// крок); на першому кроці (скоуп ще не прочитано) звіт іде без обгортки «k з N» і без масштабу — це 0 % старту.
    /// </remarks>
    private sealed class ChainProgress(IJobProgress inner, int done, int? total) : IJobProgress
    {
        /// <summary>Ключ каталогу «період {step} з {total}: {message}» (огляд L2).</summary>
        public const string StepKey = "jobs.chainStep";

        /// <summary>Скільки періодів ланцюжок уже зробив до цього кроку.</summary>
        public int Done { get; } = done;

        /// <summary>Скільки періодів у ланцюжку разом; <c>null</c> — ще невідомо (перший крок до читання скоупу).</summary>
        public int? Total { get; set; } = total;

        /// <inheritdoc />
        /// <remarks>
        /// ✎ Огляд L2: повідомлення кроку — у конверті «період k з N» (рядок задачі не скидає відсоток на кожному кроці —
        /// claim кроку ланцюжка лишає <c>StartedAt</c> і <c>Percent</c>). Не вміщається в межу стовпця — як є.
        /// </remarks>
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            if (Total is not { } known)
            {
                // Шкала ще невідома: без «k з N», щоб не показати «k з k»; відсоток — частка вже зробленого (на першому
                // кроці — як є).
                return inner.ReportAsync(((Done * 100) + Math.Clamp(percent, 0, 100)) / (Done + 1), message, ct);
            }

            var total = Math.Max(known, Done + 1);
            var wrapped = message;
            if (message is not null && JobProgressMessageCodec.TryDecode(message, out var decoded))
            {
                var step = JobProgressMessageCodec.Encode(new JobProgressMessageEnvelope(
                    StepKey,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["step"] = (Done + 1).ToString(CultureInfo.InvariantCulture),
                        ["total"] = total.ToString(CultureInfo.InvariantCulture),
                    },
                    decoded));
                if (step.Length <= JobProgressMessageCodec.MaxEncodedLength)
                {
                    wrapped = step;
                }
            }

            return inner.ReportAsync(((Done * 100) + Math.Clamp(percent, 0, 100)) / total, wrapped, ct);
        }
    }

    /// <summary>Прогрес однієї фази: шкала зсунута й стиснута, повідомлення назване.</summary>
    /// <param name="inner">Канал прогресу задачі.</param>
    /// <param name="floor">Скільки відсотків уже пройдено до цієї фази.</param>
    /// <param name="ceiling">Скільки відсотків відведено на кінець цієї фази.</param>
    /// <param name="phaseKey">Ключ каталогу фази — обгортає кожне повідомлення (<c>Q-326</c>).</param>
    /// <remarks>
    /// ⛔ Голе «40 %» не означає нічого: у прогоні дві фази, і перше, на що
    /// дивиться той, хто розбирає повільний прогін, — у якій він саме зараз.
    /// Оркестратор методологій свого місця в загальній шкалі не знає і знати
    /// не має — переклад його 0…100 у <c>floor…ceiling</c> живе тут.
    /// <para>
    /// ⚠ Q-151/Q-162: один документ (<c>floor=0, ceiling=100</c>) дає той
    /// самий результат, що й раніше жорстко зашите <c>100</c>, — навмисно, щоб
    /// не зламати наявний тест точних відсотків. Кілька документів ділять
    /// шкалу на рівні відрізки <c>floor…ceiling</c> між собою.
    /// </para>
    /// <para>
    /// ⚠ Q-326: <paramref name="phaseKey"/> — ключ каталогу («Methodologies:
    /// {message}»), не готовий текст. Повідомлення оркестратора
    /// (<c>CalculationOrchestrator</c>, вже структурований конверт
    /// <c>jobs.batchProgress</c>) стає <see cref="JobProgressMessageEnvelope.Inner"/>
    /// нового конверта — той самий принцип композиції, що й
    /// <c>RecalculationJob.WithDocumentPrefix</c>, лише на іншому шарі.
    /// </para>
    /// </remarks>
    private sealed class PhaseProgress(IJobProgress inner, int floor, int ceiling, string phaseKey)
        : IJobProgress
    {
        /// <inheritdoc />
        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            var scaled = floor + (Math.Clamp(percent, 0, 100) * (ceiling - floor) / 100);

            // ⚠ `message` тут — ЗАВЖДИ структурований конверт: єдиний
            // викликач цього класу (`orchestrator.RunAsync`, тобто
            // `CalculationOrchestrator`) уже перейшов на `ReportKeyAsync`
            // (`Q-326`). `TryDecode` про всяк випадок захищає від
            // непередбаченого прямого виклику з готовим текстом — тоді
            // фазовий конверт лишається без `Inner`.
            var envelope = message is not null && JobProgressMessageCodec.TryDecode(message, out var decoded)
                ? new JobProgressMessageEnvelope(phaseKey, Inner: decoded)
                : new JobProgressMessageEnvelope(phaseKey);

            return inner.ReportAsync(scaled, JobProgressMessageCodec.Encode(envelope), ct);
        }
    }
}

/// <summary>Завдання на перерахунок.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="DocumentId">Документ; нуль — усі документи проєкту.</param>
/// <param name="PeriodKey">Період; <c>null</c> — повний рік.</param>
/// <param name="TriggeredByUserId">Хто запустив; <c>null</c> — за розкладом.</param>
/// <param name="SheetDefId">
/// Аркуш; <c>null</c> — увесь документ (поведінка до Q-331). Звужує лише те, ЩО
/// ЗАПИСУЄТЬСЯ (формули й методології, чиї цілі належать таблицям цього
/// аркуша) — входи, як і раніше, читаються з УСІХ таблиць документа: формула
/// цього аркуша має право читати сусідній (`ReferenceResolver.FindTable`
/// резолвить <c>[SheetCode].[TableCode]</c> у БУДЬ-ЯКИЙ аркуш документа), і
/// звузити читання означало б порахувати з частково застарілих входів —
/// тихо неправильне число замість «кнопка ширша за назву» (Q-327).
/// </param>
/// <param name="FanOutParentJobId">
/// Батьківська задача-розклад (P4): кладе <c>FanOutAsync</c> у дочірні, щоб статус
/// батька рахував їх (<c>IJobProgressStore.GetFanOutAsync</c>, JSON <c>fanOutParentJobId</c>).
/// </param>
/// <param name="RecalculateSubmitted">
/// ✎ B63: системний перерахунок залежних (RT-25, HSE400 3b, FLERT F6) пише й подані аркуші (журнал + позначка);
/// затверджені — ніколи. ✎ B83: так само автоперерахунок збору PI і синхронізації подій (<c>CalculationTrigger.RequestAsync</c>).
/// ✎ B79: на правило запису більше НЕ впливає — подані аркуші перераховує кожен прогін, і нічний розклад теж; позначку
/// ставить зміна чисел (<see cref="Ecr.Application.Recalculation.SubmittedSheetChanges"/>). Поле лишається в payload
/// заради задач, уже поставлених у чергу, і тих, хто його ще передає.
/// </param>
/// <param name="Year">
/// ✎ LAND-YEAR B59: лише з <paramref name="PeriodKey"/> = <c>null</c> — «увесь рік», звужений до ЦЬОГО календарного року
/// (кнопка «Перерахувати» документа річної розкладки: усі 12 місяців). <c>null</c> — усі періоди документа. ✎ PSS-15: нічний
/// розклад (<paramref name="Nightly"/>) — у дочірній задачі завжди явний рік: поточний рік проєкту чи попередній у його
/// пільговому строку (<c>Project.YearGraceOffsetDays</c>, <c>D-562</c>).
/// </param>
/// <param name="ForeignOwnerPass">
/// ✎ LAND-YEAR L2b: другий прохід читача результатів іншого періоду (ставить сам прогін після перемикання); нових проходів не ставить.
/// </param>
/// <param name="SourceHop">
/// B179 L2: ланка ланцюжка документів-джерел, з якою поставлено перерахунок залежного (<c>CalculationTrigger</c>); 0 —
/// будь-яка інша постановка. Хук завершення несе її далі.
/// </param>
/// <param name="IncludeApproved">
/// ✎ B80 N5 (D-554, рішення людини 19.11): ЯВНИЙ перерахунок і ЗАТВЕРДЖЕНИХ аркушів — лише адміністратор з правом
/// <c>Calculation.RecalculateApprovedDependents</c> (право перевіряє ендпоінт). Без нього затверджені пропускаються завжди
/// (D-493/D-510). Окрема ціль черги (<c>…-dependents-approved</c>): задача з перекриттям не зливається з задачею без нього.
/// </param>
/// <param name="OverrideUserId">Хто ввімкнув перекриття (для журналу записів аркушів).</param>
/// <param name="OverrideReason">Причина перекриття (обов'язкова; у журнал записів аркушів).</param>
/// <param name="ChainAfter">
/// ✎ PSS-04 (<c>D-561</c>): крок ланцюжка (перерахунок документа за кілька періодів у черзі в базі) — останній уже
/// зроблений період; цей крок рахує ПЕРШИЙ період скоупу після нього. <c>null</c> — перший крок (чи не ланцюжок).
/// </param>
/// <param name="ChainDone">✎ PSS-04: періоди, які ланцюжок уже зробив актуальними (шкала прогресу «місяць k з N»).</param>
/// <param name="ChainElapsedMs">
/// ✎ Огляд M2: сума часу РОБОТИ зроблених кроків, мс (без очікувань у черзі між ними) — вимір ПРД-13 після останнього.
/// </param>
/// <param name="ChainTotal">
/// ✎ L1b / D3: скільки періодів у ланцюжку разом (зроблені + цей + решта), як їх побачив попередній крок, — шкала «період k
/// з N» відома з ПЕРШОГО звіту кроку, а не лише після перечитування скоупу. <c>null</c> — перший крок (чи не ланцюжок).
/// </param>
/// <param name="Nightly">
/// ✎ PSS-15 (<c>D-562</c>): задача нічного розкладу (<c>NightlyRecalculationScheduling</c>). Розклад проєкту без явного
/// <paramref name="Year"/> — поточний рік проєкту в його поясі і попередній, поки триває його пільговий строк
/// (<c>RecalculationJob.NightlyYears</c>), окремою документною задачею на рік; ставить лише документи, які треба рахувати
/// (<see cref="NightlySkipUnchanged"/>). Дочірні задачі несуть позначку далі (лише для діагностики).
/// </param>
/// <param name="NightlySkipUnchanged">
/// ✎ PSS-15: пропускати документи, входи яких не змінились після актуального прогону (<c>Jobs:NightlyRecalculation:SkipUnchanged</c>,
/// типово <c>true</c>). <c>false</c> — рахувати всі відкриті періоди років розкладу щоночі. Поза нічним розкладом не діє.
/// </param>
/// <param name="SkipUnchanged">
/// ✎ PERF-01 (<c>D-564a</c>): кнопка «Перерахувати» документа річної розкладки — лише місяці, чиї входи змінились після їхнього
/// актуального прогону (+ місяць на екрані, без актуального/з проваленим прогоном). Діє лише в ланцюжку черги в базі; Quartz —
/// увесь рік, як доти.
/// </param>
/// <param name="ForceFull">
/// ✎ PERF-01: «Повний перерахунок» (право <c>Calculation.Recalculate</c>, перевіряє обробник) — усі відкриті місяці, а злиття в
/// ланцюжок, що йде, починає його з першого місяця (як H1 до <c>D-564b</c>).
/// </param>
/// <param name="ScreenPeriods">✎ PERF-01: місяці на екрані натискань (злиття постановок — об'єднання): рахуються завжди.</param>
/// <param name="ChainPlan">
/// ✎ PERF-01/02: періоди, які ланцюжок ще має порахувати (з цим кроком), за зростанням; <c>null</c> — перший крок (рішення
/// ще не ухвалено) чи старий payload (усі відкриті після <see cref="ChainAfter"/>).
/// </param>
/// <param name="ChainSkipped">✎ PERF-01: місяці, пропущені як незмінні (підсумок задачі <c>jobs.recalcSkippedUnchanged</c>).</param>
/// <param name="ChainRecheck">
/// ✎ PERF-02 (<c>D-564b</c>): у ланцюжок злилося нове натискання — наступний крок перевіряє зроблені й пропущені місяці
/// детектором і дописує змінені (а не починає з першого місяця).
/// </param>
/// <param name="CascadePeriods">
/// ✎ PERF-06 (<c>D-566</c>, рішення людини 22.11): каскад від правки документа-джерела B179 — ЛИШЕ ці періоди року
/// споживача (ті, що читають змінені місяці джерела) і пізніші місяці, що читають їхні обчислені числа; постановки за вікно
/// каскаду зливаються об'єднанням (<c>CalculationTrigger.RequestSourceCascadeAsync</c>). <c>null</c> — не каскад.
/// </param>
/// <param name="CascadeAppend">
/// ✎ PERF-06 (<c>D-566</c>): періоди каскаду, що злилися в ланцюжок, який уже йде (між кроками чи позаду кроку), — наступний
/// крок дописує їх у план ЗАВЖДИ, і вже зроблені теж (джерело змінилося після їхнього кроку), без детектора й без повернення
/// на перший місяць.
/// </param>
/// <param name="CascadeSeeds">
/// ✎ PERF-06 (<c>D-566</c>, огляд F2): місяці каскаду, які САМІ не рахуються (їх уже покриває задача «з затвердженими»
/// <c>-dependents-approved</c> — надмножина роботи), але пізніші місяці, що читають їхні обчислені числа, — так: задача
/// названого періоду будить лише ранніших читачів. Злиття — об'єднанням.
/// </param>
public sealed record RecalculationRequest(
    int ProjectId,
    long DocumentId,
    int? PeriodKey,
    int? TriggeredByUserId,
    int? SheetDefId = null,
    string? FanOutParentJobId = null,
    bool RecalculateSubmitted = false,
    int? Year = null,
    bool ForeignOwnerPass = false,
    int SourceHop = 0,
    bool IncludeApproved = false,
    int? OverrideUserId = null,
    string? OverrideReason = null,
    int? ChainAfter = null,
    int[]? ChainDone = null,
    long? ChainElapsedMs = null,
    int? ChainTotal = null,
    bool Nightly = false,
    bool NightlySkipUnchanged = false,
    bool SkipUnchanged = false,
    bool ForceFull = false,
    int[]? ScreenPeriods = null,
    int[]? ChainPlan = null,
    int[]? ChainSkipped = null,
    bool ChainRecheck = false,
    int[]? CascadePeriods = null,
    int[]? CascadeAppend = null,
    int[]? CascadeSeeds = null)
{
    /// <summary>
    /// ✎ B59: нижня межа ключа періоду скоупу (включно); без року — <see cref="int.MinValue"/>.
    /// ✎ PSS-04: крок ланцюжка — лише періоди ПІСЛЯ <see cref="ChainAfter"/>.
    /// </summary>
    /// <remarks>
    /// ✎ PERF-01/02 (<c>D-564a</c>/<c>D-564b</c>): ланцюжок із планом (<see cref="ChainPlan"/>) чи з перевіркою злиття
    /// (<see cref="ChainRecheck"/>) бачить усі відкриті періоди року — план може дописати й уже зроблений місяць.
    /// ✎ PERF-06 (<c>D-566</c>, огляд F3): так само ланцюжок зі злитими періодами каскаду (<see cref="CascadeAppend"/>, і
    /// старий payload без плану) — дописаний місяць може бути не пізнішим за <see cref="ChainAfter"/>.
    /// </remarks>
    internal int FromKey => PeriodKey is not null
        ? int.MinValue
        : Math.Max(
            Year is { } y ? y * 100 : int.MinValue,
            ChainAfter is { } after && ChainPlan is null && !ChainRecheck && CascadeAppend is null ? after + 1 : int.MinValue);

    /// <summary>✎ B59: верхня межа ключа періоду скоупу (включно); без року — <see cref="int.MaxValue"/>.</summary>
    internal int ToKey => PeriodKey is null && Year is { } y ? (y * 100) + 99 : int.MaxValue;
}
