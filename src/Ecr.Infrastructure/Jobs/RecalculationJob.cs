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
/// </remarks>
public sealed class RecalculationJob(
    EcrDbContext db,
    ICalculationRunner orchestrator,
    RunCalculationHandler runs,
    RecalculationService formulas,
    Domain.Abstractions.IClock clock) : IRecalculationJob
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

        var run = new Domain.Entities.Calculations.CalculationRun(
            projectId, request.PeriodKey, request.TriggeredByUserId, clock.UtcNow);

        try
        {
            // ⛔ Створення й ПЕРШЕ збереження — ВСЕРЕДИНІ `try`, а не до нього.
            // Раніше стояли до `try`: коли сам `INSERT` провалювався (рівно
            // так і сталося з `ProjectId = 0` вище), виняток летів МИМО catch
            // нижче — і задача лишалася `Running` назавжди, хоча catch
            // виглядав так, ніби мав це перехопити.
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

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

            var totalProfile = new ModuleProfile();

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

                // ⛔ ОДИН прогін (`run.Id`) на всі документи — `CalculationRun`
                // прив'язаний до проєкту й періоду (`FK_CalculationRun_Project`),
                // не до документа. Профілі модулів зводяться в один сумарний
                // запис нижче — `ModuleProfile.Record` акумулює за кодом
                // модуля, тож повторний виклик на кожен документ саме те, для
                // чого метод і існує.
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
                            ct)
                        .ConfigureAwait(false);

                    totalProfile.Merge(profile);
                }
            }

            // Завершення — прикладний сценарій: профіль і перемикання
            // актуальності однією транзакцією (ФВ-9.11).
            await runs.CompleteAsync(run.Id, totalProfile, ct).ConfigureAwait(false);
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
            if (run.Id > 0)
            {
                db.CalculationRuns.Attach(run);
                run.Complete("Failed", clock.UtcNow, profileJson: null, errorMessage: ex.Message);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            throw;
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
        var scopesQuery = db.TableInstances
            .AsNoTracking()
            .Where(i => i.DocumentId == request.DocumentId
                        && (request.PeriodKey == null || i.PeriodKeyValue == request.PeriodKey));

        if (request.SheetDefId is { } scopeSheetId)
        {
            scopesQuery = scopesQuery.Where(i =>
                db.TableDefs.Any(td => td.Id == i.TableDefId && td.SheetDefId == scopeSheetId));
        }

        var scopes = await scopesQuery
            .Select(i => i.PeriodKeyValue)
            .Distinct()
            // За зростанням ДО стелі: на межі беруться найраніші періоди —
            // ті, від яких рахуються наступні (`[Period:-1]`), — а не
            // довільні (EF 10102).
            .OrderBy(key => key)
            .Take(MaxBindings)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. scopes.Where(key => !refused.Contains(key)).OrderBy(key => key)];
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

    /// <summary>Усі документи проєкту — для перерахунку «на весь проєкт».</summary>
    /// <remarks>
    /// Q-151/Q-162: саме цей перелік замінює «нуль документів» на «усі
    /// документи проєкту», коли <c>DocumentId</c> у завданні — 0 або менше.
    /// </remarks>
    private async Task<IReadOnlyList<long>> ProjectDocumentIdsAsync(int projectId, CancellationToken ct)
        => await db.Documents
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
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
public sealed record RecalculationRequest(
    int ProjectId,
    long DocumentId,
    int? PeriodKey,
    int? TriggeredByUserId,
    int? SheetDefId = null,
    int? ApprovedBy = null);
