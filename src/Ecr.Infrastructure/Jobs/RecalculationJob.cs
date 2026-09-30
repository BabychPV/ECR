using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
public sealed class RecalculationJob(
    EcrDbContext db,
    ICalculationRunner orchestrator,
    RunCalculationHandler runs,
    RecalculationService formulas,
    Domain.Abstractions.IClock clock,
    IBackgroundJobScheduler? jobs = null,
    RecalculationBudgetMonitor? budget = null) : IRecalculationJob
{
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

        // ⛔ P4 ФВ-9.8: проєкт — лише розклад на документні задачі, без прогону тут.
        if (request.DocumentId <= 0 && jobs is not null)
        {
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
        await using var documentLock = await AcquireDocumentLockAsync(request.DocumentId, ct).ConfigureAwait(false);

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
        var startedAt = clock.UtcNow;
        var periodRuns = new SortedDictionary<int, (Domain.Entities.Calculations.CalculationRun Run, ModuleProfile Profile)>();
        Domain.Entities.Calculations.CalculationRun? yearRun = null;

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
                var refused = await RefusedPeriodsAsync(docRequest, ct).ConfigureAwait(false);

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

                var cells = await FormulasAsync(docRequest, periods, ct).ConfigureAwait(false);

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

                var bindingsByPeriod = await BindingsAsync(docRequest, periods, ct).ConfigureAwait(false);

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
                    ? (IReadOnlyList<int>)[namedPeriod]
                    : periods;

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

                    runProfile.Merge(profile);
                }
            }

            // ⚠ Рік, у якому немає жодного періоду в скоупі (жодного
            // екземпляра таблиць або всі періоди закриті), лишає по собі один
            // прогін без періоду — як і раніше: задача за розкладом мусить
            // лишати слід (профіль пишеться завжди, J-1), а результатів у
            // такого прогону немає, тож «періоду 0» він не наповнить.
            if (periodRuns.Count == 0)
            {
                yearRun = NewRun(periodKey: null);
                db.CalculationRuns.Add(yearRun);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await runs.CompleteAsync(yearRun.Id, new ModuleProfile(), ct).ConfigureAwait(false);
            }

            // Завершення — прикладний сценарій: профіль і перемикання
            // актуальності однією транзакцією (ФВ-9.11) — на кожен період.
            foreach (var (run, profile) in periodRuns.Values)
            {
                await runs.CompleteAsync(run.Id, profile, ct).ConfigureAwait(false);
            }
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
                foreach (var run in started)
                {
                    db.CalculationRuns.Attach(run);
                    run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: ex.Message);
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            throw;
        }

        // ПРД-13 (НФ-8.6.4): вимір ПІСЛЯ завершення — лише читає годинник, логіки перерахунку не торкається.
        // Провалена задача сюди не доходить: тривалість до відмови не є тривалістю перерахунку.
        if (budget is not null)
        {
            await budget
                .ObserveAsync(
                    projectId, request.DocumentId, fullYear: request.PeriodKey is null, clock.UtcNow - startedAt, progress, ct)
                .ConfigureAwait(false);
        }
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
    /// автора й погодження (<c>ApprovedBy</c>, <c>ApprovalId</c>,
    /// <c>ApprovalReason</c>, аудит S1). Погодження використане ОДИН раз —
    /// обробником, до постановки батька; дочірні лише несуть його факт, інакше
    /// гейт стану періоду (<see cref="RefusedPeriodsAsync"/>) відмовив би кожному
    /// документу законно погодженого перерахунку закритого періоду.
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

        foreach (var documentId in documentIds)
        {
            var child = request with { DocumentId = documentId };

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
                        ["count"] = documentIds.Count.ToString(CultureInfo.InvariantCulture),
                    })),
                ct)
            .ConfigureAwait(false);
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
    /// <param name="ct">Токен скасування.</param>
    private async Task<int> FormulasAsync(
        RecalculationRequest request, IReadOnlyList<int> periods, CancellationToken ct)
    {
        var written = 0;

        foreach (var period in periods)
        {
            written += await formulas
                .RecalculateAllAsync(
                    request.DocumentId, new PeriodKey(period), ct, request.SheetDefId)
                .ConfigureAwait(false);
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
    private async Task<IReadOnlyList<int>> ScopePeriodsAsync(
        RecalculationRequest request, IReadOnlySet<int> refused, CancellationToken ct)
    {
        Task<List<int>> ScopesAsync(IQueryable<Ecr.Domain.Entities.Documents.TableInstance> instances)
        {
            var scopesQuery = instances
                .Where(i => request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey);

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
        // doc.Period, дає той самий скоуп, що й до O3d.
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
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<CalculationBindingRef>>> BindingsAsync(
        RecalculationRequest request, IReadOnlyList<int> periods, CancellationToken ct)
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
        var instancesQuery = db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == request.DocumentId
                        && scopeKeys.Contains(i.PeriodKeyValue));

        if (request.SheetDefId is { } bindingSheetId)
        {
            instancesQuery = instancesQuery.Where(i =>
                db.TableDefs.Any(td => td.Id == i.TableDefId && td.SheetDefId == bindingSheetId));
        }

        var instances = await instancesQuery
            .OrderBy(i => i.PeriodKeyValue)
            .ThenBy(i => i.Id)
            .Take(MaxBindings)
            .Select(i => new InstanceRow(i.Id, i.TableDefId, i.PeriodKeyValue))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return ReadOnlyDictionary<int, IReadOnlyList<CalculationBindingRef>>.Empty;
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

        // ⚠ `Distinct` — усередині групи періоду, а не над плоским набором:
        // однакова пара (екземпляр, методологія) неможлива в двох періодах,
        // бо екземпляр належить рівно одному періоду, але робити `Distinct`
        // після групування чесніше — воно тоді означає рівно те, що написано.
        return instances
            .SelectMany(i => bindings
                .Where(b => b.TableDefId == i.TableDefId)
                .Select(b => new PeriodBindingRow(
                    i.PeriodKeyValue, new CalculationBindingRef(i.Id, b.MethodologyId))))
            .GroupBy(row => row.PeriodKeyValue)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CalculationBindingRef>)
                    [.. group.Select(row => row.Binding).Distinct()]);
    }

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
    private async Task<IReadOnlySet<int>> RefusedPeriodsAsync(
        RecalculationRequest request, CancellationToken ct)
    {
        var states = await db.Periods
            .AsNoTracking()
            .Where(p => db.Documents.Any(d => d.Id == request.DocumentId && d.ProjectId == p.ProjectId)
                        && (request.PeriodKey == null || p.PeriodKeyValue == request.PeriodKey))
            .Select(p => new PeriodStateRow(p.PeriodKeyValue, p.State))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var submitted = await db.ApprovalStates
            .AsNoTracking()
            .Where(s => s.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || s.PeriodKey == request.PeriodKey)
                        && (s.Status == Domain.Enums.DocumentStatus.Submitted
                            || s.Status == Domain.Enums.DocumentStatus.Approved))
            .Select(s => s.PeriodKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var submittedKeys = submitted.ToHashSet();
        var approved = request.ApprovedBy is not null;
        var refused = new HashSet<int>();

        foreach (var period in states)
        {
            var denial = RecalculationWritePolicy.Check(
                period.State, submittedKeys.Contains(period.PeriodKeyValue), approved);

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

    /// <summary>Ресурс <c>sp_getapplock</c> для перерахунку документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <returns>Ім'я ресурсу.</returns>
    public static string DocumentLockResource(long documentId) => RecalculationDocumentLock.Resource(documentId);

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

    /// <summary>Екземпляр таблиці документа разом із періодом, якому він належить.</summary>
    /// <remarks>
    /// ⚠ <paramref name="PeriodKeyValue"/> доданий не для зручності: без нього
    /// прив'язки не можна розкласти за періодами, а саме плаский набір
    /// прив'язок з одним спільним ключем періоду й був дефектом річного прогону.
    /// </remarks>
    /// <param name="Id">Ідентифікатор екземпляра.</param>
    /// <param name="TableDefId">Опис таблиці.</param>
    /// <param name="PeriodKeyValue">Період екземпляра.</param>
    private sealed record InstanceRow(long Id, int TableDefId, int PeriodKeyValue);

    /// <summary>Прив'язка методології до опису таблиці.</summary>
    private sealed record BindingRow(int TableDefId, int MethodologyId);

    /// <summary>Прив'язка разом із періодом, у якому вона рахується.</summary>
    private sealed record PeriodBindingRow(int PeriodKeyValue, CalculationBindingRef Binding);

    /// <summary>Стан одного періоду — для гейту запису.</summary>
    private sealed record PeriodStateRow(int PeriodKeyValue, Domain.Enums.PeriodState State);

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
/// <param name="ApprovedBy">
/// Хто погодив перерахунок ЗАКРИТОГО періоду; <c>null</c> — погодження немає.
/// ⛔ Поле заведене разом із гейтом стану періоду і не є декорацією:
/// <c>RunCalculationHandler</c> уже клав у payload <c>approvedBy</c>, і воно
/// тихо зникало при розборі, бо в цьому записі його не існувало. Без нього
/// гейт нижче відмовляв би й законно погодженому перерахунку — тобто ламав би
/// єдиний штатний шлях виправити закритий період (ФВ-9.7).
/// ⚠ Саме <c>ApprovedBy</c>, а не <c>ApprovedByUserId</c>: ім'я мусить збігтися
/// з тим, що кладе в чергу обробник, інакше JSON не зв'яжеться і поле знову
/// буде мовчазним нулем.
/// </param>
/// <param name="SheetDefId">
/// Аркуш; <c>null</c> — увесь документ (поведінка до Q-331). Звужує лише те, ЩО
/// ЗАПИСУЄТЬСЯ (формули й методології, чиї цілі належать таблицям цього
/// аркуша) — входи, як і раніше, читаються з УСІХ таблиць документа: формула
/// цього аркуша має право читати сусідній (`ReferenceResolver.FindTable`
/// резолвить <c>[SheetCode].[TableCode]</c> у БУДЬ-ЯКИЙ аркуш документа), і
/// звузити читання означало б порахувати з частково застарілих входів —
/// тихо неправильне число замість «кнопка ширша за назву» (Q-327).
/// </param>
/// <param name="ApprovalId">
/// Використане погодження (<c>calc.RecalculationApproval</c>); кладе
/// <c>RunCalculationHandler</c>. ⚠ P4 ФВ-9.8: поле існує, щоб дочірні документні
/// задачі несли слід погодження разом з <paramref name="ApprovedBy"/>, а не
/// губили його при розборі.
/// </param>
/// <param name="ApprovalReason">Причина погодження — той самий слід, що й <paramref name="ApprovalId"/>.</param>
public sealed record RecalculationRequest(
    int ProjectId,
    long DocumentId,
    int? PeriodKey,
    int? TriggeredByUserId,
    int? SheetDefId = null,
    int? ApprovedBy = null,
    long? ApprovalId = null,
    string? ApprovalReason = null);
