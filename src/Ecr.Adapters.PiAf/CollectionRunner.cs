using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Виконує збір: ідемпотентно, з catch-up і журналом покриття (ФВ-11.3).
/// </summary>
/// <remarks>
/// Обслуговування AF відбуватиметься незалежно від нашої згоди, тому простій
/// джерела має бути **затримкою, а не втратою**. Ознака здоров'я — журнал
/// покриття, а не тиша: система, яка «нічого не повідомляє», і система, яка
/// «нічого не зібрала», ззовні виглядають однаково.
/// <para>
/// ⛔ Рівно одна відмова з цього правила випадає — <c>401</c>/<c>403</c>
/// (<c>H-20</c>). Вона не затримка: облікові дані не полагодяться самі, і
/// наздоганяння стукатиме в ті самі двері щоночі, щоразу рапортуючи успіх із
/// нулем рядків. Такий прогін обривається одразу, стає <c>Failed</c> і не
/// лишає по собі роботи в черзі.
/// </para>
/// </remarks>
public sealed partial class CollectionRunner(
    IEnumerable<IExternalDataSource> sources,
    SourceUnitConverter unitConverter,
    CatchUpPlanner catchUp,
    ICollectionStore store,
    TimeSpan? maxRunDuration = null,
    ILogger<CollectionRunner>? logger = null,
    int? maxPagesPerRead = null,
    int? maxParallelReads = null) : ICollectionRunner
{
    /// <summary>Лог збоїв прогону; без реєстрації (тести) — порожній.</summary>
    private readonly ILogger log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Стеля точок на один запит до джерела.</summary>
    /// <remarks>
    /// PI AF на надмірний запит відповідає деградацією **всім** клієнтам,
    /// зокрема тим, що не наші. Батч обмежений з поваги до чужих клієнтів, а
    /// не з любові до сторінкування.
    /// </remarks>
    public const int MaxPointsPerRequest = 5_000;

    /// <summary>Стеля тривалості ОДНОГО прогону збору (Q-250).</summary>
    /// <remarks>
    /// ⚠ П'ятнадцять хвилин — не з довідника постачальника (задокументованого
    /// SLA відповіді PI Web API в цьому репозиторії немає), а практичний
    /// поріг: `GetAsync` у гіршому разі — це ~96 с на один запит
    /// (30-секундний таймаут HttpClient (`Q-250`, `DependencyInjection.cs`)
    /// плюс паузи ретраю 2 с і 4 с), а `RunAsync` іде по інтервалах
    /// наздоганяння (до 45 діб, див. <see cref="CatchUpLookback"/>) — без
    /// стелі «напівживе» джерело (відповідає, але повільно) тримало б воркер
    /// Quartz годинами замість хвилин.
    /// <para>
    /// ✎ P7: для PI Web API (адаптер з <see cref="IBatchCollectionSource"/>)
    /// читання тепер ГРУПОВЕ й паралельне — один <c>streamsets/recorded</c> на
    /// інтервал для всіх атрибутів, до <see cref="DefaultMaxParallelReads"/>
    /// інтервалів одночасно, тож за ліміт прогону вміщується наздоганяння на
    /// сотні інтервалів. Послідовно, по запиту на пару інтервал/атрибут, читає
    /// лише адаптер без цієї здатності (PiSqlClient) — для нього п'ятнадцять
    /// хвилин так само дають запас на кілька десятків повільних пар.
    /// </para>
    /// </remarks>
    public static TimeSpan DefaultMaxRunDuration => TimeSpan.FromMinutes(15);

    /// <summary>Тривалість цього прогону: параметр конструктора або дефолт.</summary>
    /// <remarks>
    /// ⚠ Необов'язковий параметр конструктора, а не мутабельне статичне поле:
    /// тести підставляють коротший ліміт, не ділячи один спільний стан між
    /// паралельними прогонами. DI (<c>AddScoped&lt;ICollectionRunner,
    /// CollectionRunner&gt;</c>) не знає типу <c>TimeSpan?</c> і підставляє
    /// значення параметра за замовчуванням — це штатна поведінка вбудованого
    /// контейнера, не обхідний прийом.
    /// </remarks>
    private readonly TimeSpan runDuration = maxRunDuration ?? DefaultMaxRunDuration;

    /// <summary>Стеля сторінок на одну пару інтервал/атрибут за прогін (аудит B3).</summary>
    /// <remarks>
    /// ⚠ Судження, не вимога: 200 сторінок по <see cref="MaxPointsPerRequest"/>
    /// — мільйон точок, тобто майже два роки хвилинного тега. Це запобіжник від
    /// джерела, що віддає нескінченно (тоді годинник прогону спрацював би
    /// значно пізніше), а не робочий режим: прочитане покривається, решту
    /// бере наздоганяння з місця зупинки.
    /// </remarks>
    public const int DefaultMaxPagesPerRead = 200;

    /// <summary>Стеля сторінок цього прогону: параметр конструктора (тести) або дефолт.</summary>
    private readonly int maxPages = maxPagesPerRead is > 0 ? maxPagesPerRead.Value : DefaultMaxPagesPerRead;

    /// <summary>
    /// Наскільки глибоко кожен прогін заглядає назад по прогалини.
    /// </summary>
    /// <remarks>
    /// ⚠ Сорок п'ять діб — це звітний місяць плюс пільговий строк. Дірка,
    /// старша за це, вже не наздоганяння: період за неї або закрито, або
    /// закривають зараз, і латати його мовчки фоновою задачею не можна —
    /// потрібна людина.
    /// </remarks>
    public static TimeSpan CatchUpLookback => TimeSpan.FromDays(45);

    private const string SourceUnavailable = "ECR-INT-0503";

    /// <summary>Джерело відмовило в автентифікації — не те саме, що недоступність (<c>H-20</c>).</summary>
    private const string AuthenticationRefused = "ECR-INT-0502";

    /// <inheritdoc />
    public async Task RunAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var entity = await store.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
                     ?? throw Unavailable(
                         $"Сутність джерела {sourceEntityId} не існує або вимкнена: збирати нічого.",
                         sourceEntityId,
                         "err.ECR-INT-0503.sourceEntityUnavailable");

        var dataSource = await store.FindDataSourceAsync(entity.DataSourceId, ct).ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Джерело {entity.DataSourceId} не існує або вимкнене.",
                             sourceEntityId,
                             // Той самий ключ і те саме речення, що SqlDataSource.cs: той самий факт.
                             "err.ECR-INT-0503.sourceMissing",
                             ("dataSourceId", entity.DataSourceId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // ⚠ Транспорт — це НАЛАШТУВАННЯ, а не гілка коду (ФВ-11.2). Адаптер
        // обирається за Transport джерела; додати третій транспорт означає
        // зареєструвати ще одну реалізацію, а не правити цей метод.
        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Транспорт {dataSource.Transport} не зареєстровано.",
                          sourceEntityId,
                          "err.ECR-INT-0503.transportNotRegistered",
                          ("transport", dataSource.Transport.ToString()));

        var maps = await store.GetFieldMapsAsync(sourceEntityId, ct).ConfigureAwait(false);
        var units = await unitConverter.UnitsAsync(ct).ConfigureAwait(false);
        var paths = Paths(entity, maps);

        var work = await PlanAsync(sourceEntityId, fromUtc, toUtc, ct).ConfigureAwait(false);
        var isCatchUp = work.Count > 1;

        var runId = await store
            .StartRunAsync(sourceEntityId, work[0].FromUtc, toUtc, isCatchUp, null, ct)
            .ConfigureAwait(false);

        var covered = new List<TimeInterval>();
        var retrieved = 0;
        string? failureCode = null;

        // ⚠ U12: причина — конверт (ключ + параметри, `Q-326`), а не готове
        // речення: `ErrorMessage` прогону читають мовою ЧИТАЧА (шухляда
        // прогону, зведення), а мова в момент запису невідома.
        JobProgressMessageEnvelope? failureReason = null;
        var step = 0;

        // ⚠ «Джерело нас у цьому прогоні вже пускало». Саме цим відрізняється
        // прострочений квиток від неправильних облікових даних: перший
        // з'являється ПІСЛЯ успішних відповідей, другі — з першої ж.
        var accepted = false;

        // ⚠ Друга спроба на прогін одна. Без лічильника «перездобути квиток»
        // перетворилося б на нескінченний цикл рівно тоді, коли джерело
        // відмовляє стало.
        var reacquired = false;

        // Атрибути, чиї мапінги цей прогін поставив на паузу (ФВ-16.9).
        var pausedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ⚠ Інтервал, що читається зараз, і межа, до якої він прочитаний
        // ДОСТОВІРНО (усіма атрибутами). Потрібні, коли прогін обривається
        // посеред сторінкування (watchdog, скасування, збій): покриття
        // пишеться за фактично прочитане, і наздоганяння продовжує звідси, а
        // не з початку інтервалу (аудит B3).
        TimeInterval? inFlight = null;
        var inFlightReached = DateTime.MinValue;

        List<TimeInterval> CoveredWithInFlight()
            => inFlight is { } open && inFlightReached > open.FromUtc
                ? [.. covered, new TimeInterval(open.FromUtc, inFlightReached)]
                : covered;

        // ⚠ Годинник прогону (Q-250): рахує ЛИШЕ звідси, а не з початку
        // методу — підготовка вище (пошук сутності, мапінгів, планування)
        // у джерело не ходить і в цей ліміт не входить. Пов'язаний із
        // зовнішнім `ct`: скасування задачі ззовні (Quartz `Interrupt`)
        // скасовує й watchdog теж, і catch нижче навмисно відрізняє один
        // випадок від іншого за станом САМЕ зовнішнього токена.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(runDuration);
        var runToken = watchdog.Token;

        // P7: адаптер, що читає атрибути пакетом, отримує один запит на
        // інтервал замість одного на атрибут, а наступні інтервали читаються
        // наперед (не більше ParallelReads одночасно). Адаптер без цієї
        // здатності (PiSqlClient) читається, як і раніше, послідовно.
        var batch = adapter as IBatchCollectionSource;

        // Одна прочитана сторінка одного атрибута: зберегти прочитане й
        // вирішити, чи читати далі (true — є хвіст, курсор просунуто).
        // Спільна для послідовного й пакетного читання — B3 живе в одному місці.
        async Task<bool> ApplyPageAsync(PathCursor state, ReadOutcome outcome, TimeInterval interval)
        {
            if (outcome.Collected is not { } result)
            {
                failureCode ??= outcome.ErrorCode ?? SourceUnavailable;
                failureReason ??= SourceDetail(outcome.Message);
                return false;
            }

            accepted = true;

            // ⚠ Успішні точки зберігаються НАВІТЬ при частковій
            // відмові батча: викинути прочитане через те, що хвіст
            // діапазону не дався, означало б читати його вдруге —
            // і так до наступної відмови.
            var saved = await SaveAsync(
                    runId, sourceEntityId, Fresh(result.Points, state.Carried), maps, units, pausedPaths, ct)
                .ConfigureAwait(false);
            retrieved += saved.Written;
            state.Pages++;

            if (saved.UnitChange is { } change)
            {
                failureCode ??= SourceUnitConverter.UnitChangedCode;
                failureReason ??= change;
                return false;
            }

            if (result.ErrorCode is not null)
            {
                failureCode ??= result.ErrorCode;
                return false;
            }

            if (result.FailedIntervals.Count == 0)
            {
                state.Cursor = interval.ToUtc;
                return false;
            }

            // Батч обрізано стелею: наступна сторінка — з першого
            // непрочитаного моменту. Адаптери віддають хвіст як
            // [мітка ОСТАННЬОЇ точки, кінець) — тобто межу ВКЛЮЧНО
            // (≥): точки з тією самою міткою, що не влізли в батч,
            // інакше загубилися б. Уже прочитані з цією міткою
            // наступна сторінка поверне вдруге — їх відкидає Fresh.
            var next = result.FailedIntervals.Min(i => i.FromUtc);

            if (next <= state.Cursor)
            {
                // Уся сторінка — одна мітка: ≥ не просуває курсора,
                // а > загубив би точки. Далі цей атрибут у цьому
                // інтервалі прочитати неможливо — так і пишемо.
                failureCode ??= SourceUnavailable;
                failureReason ??= Reason(
                    "jobs.collectionSameTimestamp",
                    ("path", state.Path),
                    ("limit", Number(MaxPointsPerRequest)),
                    ("cursor", Instant(state.Cursor)),
                    ("to", Instant(interval.ToUtc)));
                return false;
            }

            state.Carried = Carried(result.Points, next);
            state.Cursor = next;

            if (state.Pages >= maxPages)
            {
                failureCode ??= SourceUnavailable;
                failureReason ??= Reason(
                    "jobs.collectionPageLimit",
                    ("path", state.Path),
                    ("pages", Number(state.Pages)),
                    ("limit", Number(MaxPointsPerRequest)),
                    ("cursor", Instant(state.Cursor)),
                    ("to", Instant(interval.ToUtc)));
                return false;
            }

            return true;
        }

        // Пакетне читання інтервалу: раунд — одна сторінка кожного ще не
        // дочитаного атрибута; перший раунд уже запущено наперед. Повертає
        // межу, до якої інтервал прочитали ВСІ атрибути.
        async Task<DateTime> ReadBatchedAsync(
            TimeInterval interval, (List<PathCursor> Cursors, Task<IReadOnlyList<ReadOutcome>> Round) first)
        {
            // Атрибут на паузі (зокрема поставлений на неї вже ПІСЛЯ запуску
            // читання наперед) у цьому інтервалі не читається й тримає його
            // непокритим — як і в послідовному читанні.
            var floor = paths.Any(pausedPaths.Contains) ? interval.FromUtc : interval.ToUtc;
            var open = first.Cursors;
            var round = first.Round;

            DateTime Reached()
                => first.Cursors.Count == 0 ? floor : Min(floor, first.Cursors.Min(c => c.Cursor));

            while (open.Count > 0)
            {
                var outcomes = await round.ConfigureAwait(false);

                if (outcomes.Any(o => o.Unauthorized))
                {
                    // Та сама єдина друга спроба на прогін, що й у послідовному
                    // читанні (квиток міг протухнути), — для відмовлених атрибутів.
                    if ((accepted || outcomes.Any(o => o.Collected is not null)) && !reacquired)
                    {
                        reacquired = true;

                        var refused = Enumerable.Range(0, open.Count).Where(i => outcomes[i].Unauthorized).ToList();
                        var again = await ReadRoundAsync(
                                batch!, dataSource, sourceEntityId, [.. refused.Select(i => open[i])], interval.ToUtc, runToken)
                            .ConfigureAwait(false);

                        var merged = outcomes.ToArray();

                        for (var k = 0; k < refused.Count; k++)
                        {
                            merged[refused[k]] = again[k];
                        }

                        outcomes = merged;
                    }

                    if (outcomes.FirstOrDefault(o => o.Unauthorized) is { } denied)
                    {
                        throw await FailAuthenticationAsync(
                                runId, sourceEntityId, entity.Code, CoveredWithInFlight(), retrieved, denied.Message)
                            .ConfigureAwait(false);
                    }
                }

                var unfinished = new List<PathCursor>();

                for (var i = 0; i < open.Count; i++)
                {
                    if (!pausedPaths.Contains(open[i].Path)
                        && await ApplyPageAsync(open[i], outcomes[i], interval).ConfigureAwait(false))
                    {
                        unfinished.Add(open[i]);
                    }
                }

                inFlightReached = Reached();
                open = unfinished;

                if (open.Count > 0)
                {
                    round = ReadRoundAsync(batch!, dataSource, sourceEntityId, open, interval.ToUtc, runToken);
                }
            }

            return Reached();
        }

        try
        {
            await using var ahead = batch is null
                ? null
                : new ReadAhead(
                    work.Count,
                    ParallelReads,
                    (index, token) =>
                    {
                        List<PathCursor> cursors =
                        [
                            .. paths.Where(p => !pausedPaths.Contains(p))
                                .Select(p => new PathCursor(p, work[index].FromUtc)),
                        ];

                        return (cursors, ReadRoundAsync(
                            batch!, dataSource, sourceEntityId, cursors, work[index].ToUtc, token));
                    },
                    runToken);

            foreach (var interval in work)
            {
                inFlight = interval;
                inFlightReached = interval.FromUtc;

                // Межа, до якої інтервал прочитали ВСІ вже пройдені атрибути.
                // `step` — порядковий номер цього інтервалу в `work`.
                var reachedAll = ahead is null
                    ? interval.ToUtc
                    : await ReadBatchedAsync(interval, ahead.Take(step)).ConfigureAwait(false);

                for (var index = 0; ahead is null && index < paths.Count; index++)
                {
                    var path = paths[index];
                    var lastPath = index == paths.Count - 1;

                    // Мапінг, поставлений на паузу через зміну одиниці, у
                    // цьому прогоні більше не читається; інтервал лишається
                    // непокритим — після рішення людини його забере наздоганяння.
                    if (pausedPaths.Contains(path))
                    {
                        reachedAll = interval.FromUtc;
                        continue;
                    }

                    // ⛔ Аудит B3: інтервал читається СТОРІНКАМИ до кінця, а не
                    // одним запитом зі стелею. Раніше хвіст понад
                    // MaxPointsPerRequest лише знімав покриття, і кожен прогін
                    // перечитував ті самі перші 5000 точок — решта не
                    // збиралася НІКОЛИ, а прогін ставав «Degraded» з
                    // неправдивим «джерело недоступне».
                    var state = new PathCursor(path, interval.FromUtc);

                    while (true)
                    {
                        var page = new TimeInterval(state.Cursor, interval.ToUtc);

                        var outcome = await ReadAsync(
                            adapter, dataSource.Id, sourceEntityId, path, page, runToken).ConfigureAwait(false);

                        if (outcome.Unauthorized)
                        {
                            // ⚠ Єдиний виняток із заборони повторювати: квиток міг
                            // просто протухнути посеред довгого прогону. Запит
                            // адаптера перескладається з нуля — секрет читається на
                            // кожне звернення, — тож це справді ПЕРЕЗДОБУТТЯ, а не
                            // той самий заголовок удруге.
                            if (accepted && !reacquired)
                            {
                                reacquired = true;

                                outcome = await ReadAsync(
                                    adapter, dataSource.Id, sourceEntityId, path, page, runToken)
                                    .ConfigureAwait(false);
                            }

                            if (outcome.Unauthorized)
                            {
                                // ⛔ Прогін обривається ТУТ. Решта інтервалів і
                                // атрибутів не читається: ті самі облікові дані
                                // дадуть ту саму відмову, а прогін від цього стане
                                // лише довшим (`H-20`).
                                throw await FailAuthenticationAsync(
                                    runId, sourceEntityId, entity.Code, CoveredWithInFlight(), retrieved,
                                    outcome.Message)
                                    .ConfigureAwait(false);
                            }
                        }

                        var more = await ApplyPageAsync(state, outcome, interval).ConfigureAwait(false);

                        if (lastPath)
                        {
                            inFlightReached = Min(reachedAll, state.Cursor);
                        }

                        if (!more)
                        {
                            break;
                        }
                    }

                    reachedAll = Min(reachedAll, state.Cursor);

                    if (lastPath)
                    {
                        inFlightReached = reachedAll;
                    }
                }

                // ⛔ Покриття пишеться ЛИШЕ за фактично прочитане ВСІМА
                // атрибутами. Записане наперед покриття — це дірка, яку більше
                // ніхто не знайде: наздоганяння шукає прогалини саме тут.
                if (reachedAll > interval.FromUtc)
                {
                    covered.Add(new TimeInterval(interval.FromUtc, reachedAll));
                }

                inFlight = null;

                step++;
                await progress
                    .ReportKeyAsync(
                        Percent(step, work.Count),
                        "jobs.collectionProgress",
                        Params(("points", Number(retrieved)), ("step", Number(step)), ("total", Number(work.Count))),
                        ct)
                    .ConfigureAwait(false);
            }
        }
        catch (BusinessRuleException ex)
        {
            // Правило, що зупиняє весь прогін: те, що вже прочитано,
            // лишається, покриття за незавершені інтервали — ні. (Зміна
            // одиниці сюди більше не доходить — вона ставить на паузу лише
            // свій мапінг, див. SaveAsync.)
            await store
                .WriteCoverageAsync(runId, sourceEntityId, CoveredWithInFlight(), CancellationToken.None)
                .ConfigureAwait(false);

            await store
                .FinishRunAsync(
                    runId,
                    "Failed",
                    retrieved,
                    Encode(Reason("jobs.collectionRuleFailed", ("code", ex.ErrorCode), ("error", Trim(ex.Message)))),
                    CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // ⚠ Це спрацював НАШ watchdog (Q-250), а не зовнішнє скасування
            // задачі: за зовнішнього скасування `ct.IsCancellationRequested`
            // теж було б true, і цей `when` навмисно не ловив би виняток —
            // він пройшов би далі так само, як і до цієї зміни, а не
            // прикидався б «Degraded» прогоном, що завершився сам.
            //
            // ⛔ Не rethrow. Джерело, що відповідає, але надто повільно, —
            // це затримка (ФВ-11.3), а не збій: непрочитане нижче піде в
            // ту саму гілку `Degraded`, що й звичайна відмова джерела, і
            // наздоганяння забере його наступного разу без втрати даних.
            // Прочитані до обриву сторінки інтервалу — теж покриття: інакше
            // наступний прогін почав би цей інтервал спочатку і знову вперся б
            // у той самий ліміт (аудит B3).
            covered = CoveredWithInFlight();
            inFlight = null;

            failureCode ??= SourceUnavailable;
            failureReason ??= Reason(
                "jobs.collectionTimeout",
                ("minutes", runDuration.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // ⚠ Скасування ЗЗОВНІ (Quartz `Interrupt`, зупинка сервісу) —
            // не збій і не мовчанка (аудит B4). Прогін закривається як
            // «Degraded»: непрочитане лишилося без покриття і піде в
            // наздоганяння, як за відмови джерела. Окремого статусу
            // «Cancelled» журнал прогонів не знає
            // (`CollectionRunHandlers.KnownStates`), а вводити його — це вже
            // зміна контракту й екрана, не цього виправлення. Виняток іде
            // далі: задача мусить побачити, що її скасували.
            await CloseAfterFailureAsync(
                    runId, sourceEntityId, CoveredWithInFlight(), "Degraded", retrieved,
                    Encode(WithCode(SourceUnavailable, Reason("jobs.collectionCancelled"))),
                    ex)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not SourceAuthenticationException)
        {
            // ⛔ Аудит B4: будь-що, крім правила й watchdog, — збій сховища
            // (`DbUpdateException`), прогресу, самого збирача — раніше вилітало
            // повз `WriteCoverageAsync`/`FinishRunAsync`, і `itg.CollectionRun`
            // лишався «Running» НАЗАВЖДИ, а покриття вже прочитаних інтервалів
            // губилося. Тепер: покриття — за повністю прочитане, прогін —
            // «Failed» із причиною, виняток — далі (не ковтаємо: задача має
            // стати невдалою, а причина — потрапити в журнал задачі).
            // Відмова в автентифікації сюди не йде: її прогін уже закрито
            // (`FailAuthenticationAsync`).
            await CloseAfterFailureAsync(
                    runId, sourceEntityId, CoveredWithInFlight(), CollectionFailure.FailedStatus, retrieved,
                    Encode(Reason("jobs.collectionRunFailed", ("error", Trim(Describe(ex))))),
                    ex)
                .ConfigureAwait(false);
            throw;
        }

        try
        {
            await store.WriteCoverageAsync(runId, sourceEntityId, covered, ct).ConfigureAwait(false);

            // ⚠ Відмова джерела — «Degraded», а не «Failed», і виняток НЕ
            // кидається: діапазон лишився непокритим, наздоганяння візьме його
            // наступного разу. Це затримка, а не збій.
            await store
                .FinishRunAsync(
                    runId,
                    failureCode is null ? "Succeeded" : "Degraded",
                    retrieved,
                    failureCode is null
                        ? null
                        : Encode(WithCode(failureCode, failureReason ?? Reason("jobs.collectionSourceUnavailable"))),
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Те саме правило, що й вище: прогін не лишається «Running».
            // ⚠ Покриття вдруге НЕ пишеться (`[]`): якщо впав саме його запис,
            // повтор додав би ті самі інтервали ще раз поверх уже відстежених.
            await CloseAfterFailureAsync(
                    runId, sourceEntityId, [], CollectionFailure.FailedStatus, retrieved,
                    Encode(Reason("jobs.collectionCloseFailed", ("error", Trim(Describe(ex))))),
                    ex)
                .ConfigureAwait(false);
            throw;
        }

        await progress
            .ReportKeyAsync(
                100,
                failureCode is null ? "jobs.collectionDone" : "jobs.collectionDonePartial",
                Params(("points", Number(retrieved))),
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Конверт причини (<c>Q-326</c>): ключ каталогу й параметри підстановки.</summary>
    private static JobProgressMessageEnvelope Reason(string key, params (string Name, string Value)[] parameters)
        => new(key, parameters.Length == 0 ? null : Params(parameters));

    /// <summary>Причина відмови з кодом попереду — та сама форма «код: причина», що й до U12.</summary>
    private static JobProgressMessageEnvelope WithCode(string code, JobProgressMessageEnvelope reason)
        => new("jobs.collectionRunReason", Params(("code", code)), reason);

    /// <summary>Текст відмови від адаптера; <c>null</c> — адаптер нічого не сказав.</summary>
    /// <remarks>
    /// ⚠ Сам текст — ДАНІ джерела (мова транспорту чи сервера PI), не наше
    /// формулювання: він іде параметром як є, перекладається лише рамка.
    /// </remarks>
    private static JobProgressMessageEnvelope? SourceDetail(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? null : Reason("jobs.collectionSourceError", ("detail", Trim(detail)));

    private static Dictionary<string, string> Params(params (string Name, string Value)[] parameters)
        => parameters.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    private static string Encode(JobProgressMessageEnvelope envelope) => JobProgressMessageCodec.Encode(envelope);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Instant(DateTime value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Вільний текст — не довший за <see cref="MaxDetailLength"/> (стовпець <c>nvarchar(2000)</c>).</summary>
    private static string Trim(string text) => JobProgressMessageCodec.Shorten(text, MaxDetailLength);

    /// <summary>
    /// Закриває прогін як невдалий через відмову в автентифікації і віддає
    /// виняток, яким його треба обірвати.
    /// </summary>
    /// <param name="runId">Прогін збору.</param>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceCode">Код сутності — його шукатиме людина у зведенні.</param>
    /// <param name="covered">Інтервали, прочитані ПОВНІСТЮ до відмови.</param>
    /// <param name="retrieved">Скільки точок устигли записати.</param>
    /// <param name="detail">Текст відмови від адаптера; без стека (ФВ-6.11).</param>
    /// <returns>Виняток, який обриває прогін.</returns>
    /// <remarks>
    /// ⛔ Повертає виняток, а не кидає його сам: інакше компілятор не бачив би,
    /// що виконання далі не йде, і за викликом лишалася б гілка «збір триває»,
    /// яку ніхто ніколи не виконає, — а такі гілки згодом починають правити
    /// всерйоз.
    ///
    /// ⚠ Покриття за ПРОЧИТАНІ до відмови інтервали пишеться. Викидати його
    /// разом із прогоном означало б збирати їх удруге після того, як облікові
    /// дані полагодять.
    ///
    /// ⚠ <c>CancellationToken.None</c> навмисно: журнал прогону має закритися
    /// навіть тоді, коли задачу вже скасували, — інакше прогін лишиться
    /// «Running» назавжди, і його чекатимуть замість того, щоб дивитися на
    /// джерело.
    /// </remarks>
    private async Task<Exception> FailAuthenticationAsync(
        long runId,
        int sourceEntityId,
        string sourceCode,
        IReadOnlyList<TimeInterval> covered,
        int retrieved,
        string? detail)
    {
        var message = Compose(CollectionFailure.AuthenticationRefused(sourceCode), detail);

        await store
            .WriteCoverageAsync(runId, sourceEntityId, covered, CancellationToken.None)
            .ConfigureAwait(false);

        await store
            .FinishRunAsync(
                runId, CollectionFailure.FailedStatus, retrieved, message, CancellationToken.None)
            .ConfigureAwait(false);

        return new SourceAuthenticationException(
            AuthenticationRefused,
            message,
            new Dictionary<string, object?>
            {
                // ⚠ Лише Details несе ключ — саме `message` (вище) лишається
                // МАРКЕРНИМ текстом: CollectionFailure.IsAuthenticationRefusal
                // читає його з itg.CollectionRun.ErrorMessage за підрядком
                // AuthenticationMarker, і messageKey впливає лише на Detail
                // http-відповіді (ResolveGenericMessageAsync), не на ex.Message.
                ["messageKey"] = "err.ECR-INT-0502.authenticationRefused",
                ["sourceEntityId"] = sourceEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sourceCode"] = sourceCode,
            });
    }

    /// <summary>
    /// Закриває прогін після непередбаченого винятку: покриття за ПОВНІСТЮ
    /// прочитане, статус і причина — у <c>itg.CollectionRun</c>, сам виняток — у лог.
    /// </summary>
    /// <param name="runId">Прогін збору.</param>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="covered">Повністю прочитані інтервали; <c>[]</c> — покриття вже писали.</param>
    /// <param name="status">Статус закриття.</param>
    /// <param name="retrieved">Скільки точок устигли записати.</param>
    /// <param name="message">Причина для журналу прогону — без стека (ФВ-6.11).</param>
    /// <param name="cause">Виняток, що обірвав прогін; стек іде лише в лог.</param>
    /// <remarks>
    /// ⚠ <c>CancellationToken.None</c> — з тієї ж причини, що в
    /// <see cref="FailAuthenticationAsync"/>: журнал має закритися й тоді,
    /// коли задачу вже скасували.
    /// <para>
    /// ⛔ Власна відмова закриття (база лежить) лише логується і НЕ підміняє
    /// первинного винятку: викликач кидає далі саме причину, а не наслідок.
    /// </para>
    /// </remarks>
    private async Task CloseAfterFailureAsync(
        long runId,
        int sourceEntityId,
        IReadOnlyList<TimeInterval> covered,
        string status,
        int retrieved,
        string message,
        Exception cause)
    {
        LogRunFailed(log, runId, sourceEntityId, status, cause);

        try
        {
            await store
                .WriteCoverageAsync(runId, sourceEntityId, covered, CancellationToken.None)
                .ConfigureAwait(false);

            await store
                .FinishRunAsync(runId, status, retrieved, message, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception closeFailure)
        {
            LogRunNotClosed(log, runId, sourceEntityId, closeFailure);
        }
    }

    /// <summary>Тип і найглибше повідомлення винятку — без стека.</summary>
    /// <remarks>
    /// ⚠ Найглибше: у <c>DbUpdateException</c> власний текст — «див. внутрішній
    /// виняток», і саме внутрішній каже, ЯКЕ обмеження порушено.
    /// </remarks>
    private static string Describe(Exception ex)
        => $"{ex.GetType().Name}: {ex.GetBaseException().Message}";

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "CollectionRunner: прогін {RunId} сутності {SourceEntityId} обірвано винятком; закривається як {Status}.")]
    private static partial void LogRunFailed(
        ILogger logger, long runId, int sourceEntityId, string status, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "CollectionRunner: прогін {RunId} сутності {SourceEntityId} не вдалося закрити після збою — лишається Running.")]
    private static partial void LogRunNotClosed(
        ILogger logger, long runId, int sourceEntityId, Exception exception);

    /// <summary>Скільки символів тексту від адаптера входить у журнал прогону.</summary>
    /// <remarks>
    /// <c>itg.CollectionRun.ErrorMessage</c> — <c>nvarchar(2000)</c>. Причина
    /// відмови має вміститися РАЗОМ із нашим формулюванням, інакше запис
    /// обрізала б база, і обрізала б саме те, що ми додали.
    /// </remarks>
    private const int MaxDetailLength = 900;

    /// <summary>Наше формулювання плюс причина від джерела.</summary>
    private static string Compose(string message, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return message;
        }

        var trimmed = detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail;

        return $"{message} Причина від джерела: {trimmed}";
    }

    /// <summary>Що читати: запитаний діапазон плюс давніші прогалини.</summary>
    /// <remarks>
    /// ⚠ Давнє йде ПЕРШИМ. Прогалина потрібна звітності тим більше, чим вона
    /// старша: за свіжий діапазон звіт ще не складають, за минулий — уже
    /// складають.
    /// </remarks>
    private async Task<List<TimeInterval>> PlanAsync(
        int sourceEntityId, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var gaps = await catchUp
            .PlanAsync(sourceEntityId, fromUtc - CatchUpLookback, ct)
            .ConfigureAwait(false);

        var work = gaps
            .Where(g => g.From < fromUtc)
            .Select(g => new TimeInterval(g.From, g.To < fromUtc ? g.To : fromUtc))
            .Where(g => g.ToUtc > g.FromUtc)
            .ToList();

        // Запитаний діапазон читається ЗАВЖДИ, навіть якщо покриття за нього
        // вже є: джерело переписує значення заднім числом, і «вже збирали» не
        // означає «те саме число».
        work.Add(new TimeInterval(fromUtc, toUtc));
        return work;
    }

    /// <summary>Читає один інтервал одного атрибута, перетворюючи відмову на результат.</summary>
    /// <remarks>
    /// ⚠ Виняток джерела гаситься тут і лише тут. Вище він означав би, що
    /// недоступність AF валить усю задачу — і разом із нею інтервали, які
    /// прочиталися.
    /// </remarks>
    private static async Task<ReadOutcome> ReadAsync(
        IExternalDataSource adapter,
        int dataSourceId,
        int sourceEntityId,
        string path,
        TimeInterval interval,
        CancellationToken ct)
    {
        var request = new CollectionRequest(
            dataSourceId, sourceEntityId, path, interval.FromUtc, interval.ToUtc, MaxPointsPerRequest);

        try
        {
            return new ReadOutcome(
                await adapter.ReadAsync(request, ct).ConfigureAwait(false), null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceAuthenticationException ex)
        {
            // ⛔ Відмова в автентифікації НЕ зводиться до «джерело недоступне»
            // (`H-20`). Саме тут її раніше й губили: гілка нижче гасила будь-який
            // виняток у звичайний збій, після якого прогін ішов у наздоганяння
            // і завершувався успішно.
            return new ReadOutcome(null, ex.ErrorCode, ex.Message, Unauthorized: true);
        }
        catch (Exception ex)
        {
            // ⛔ Текст — без стека (ФВ-6.11): він іде в itg.CollectionRun, а
            // цей журнал видно в інтерфейсі обслуговування.
            return new ReadOutcome(null, SourceUnavailable, ex.Message);
        }
    }

    /// <summary>
    /// Перевіряє одиниці й зберігає точки. Мапінг, чия одиниця змінилася,
    /// стає на паузу з позначкою, а його точки не пишуться (ФВ-16.9).
    /// </summary>
    /// <remarks>
    /// ⛔ Зупиняється МАПІНГ, а не прогін: раніше `ECR-INT-0422` валив увесь
    /// збір сутності, і атрибути з правильною одиницею теж лишалися без даних.
    /// Мовчазної конверсії як не було, так і немає — жодна точка зміненого
    /// атрибута не пишеться, доки людина не вирішить.
    /// </remarks>
    private async Task<SaveOutcome> SaveAsync(
        long runId,
        int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points,
        IReadOnlyList<EntityFieldMap> maps,
        UnitCatalogSnapshot units,
        HashSet<string> pausedPaths,
        CancellationToken ct)
    {
        if (points.Count == 0)
        {
            return new SaveOutcome(0, null);
        }

        JobProgressMessageEnvelope? unitChange = null;

        foreach (var point in points)
        {
            var map = maps.FirstOrDefault(
                m => string.Equals(m.SourceField, point.SourcePath, StringComparison.OrdinalIgnoreCase));

            if (map is null
                || pausedPaths.Contains(map.SourceField)
                || SourceUnitConverter.IsDeclaredUnit(map.SourceUnitId, point.SourceUnitSymbol, units))
            {
                continue;
            }

            var actualCode = point.SourceUnitSymbol!;
            int? actualId = units.Units.TryGetValue(actualCode, out var actual) ? actual.Id : null;

            await store
                .PauseForSourceUnitChangeAsync(map.Id, actualCode, actualId, ct)
                .ConfigureAwait(false);

            pausedPaths.Add(map.SourceField);
            unitChange ??= Reason(
                "jobs.collectionUnitChanged",
                ("path", map.SourceField),
                ("actual", actualCode),
                ("declared", map.SourceUnitId?.ToString(CultureInfo.InvariantCulture) ?? "—"));
        }

        var accepted = pausedPaths.Count == 0
            ? points
            : points.Where(p => !pausedPaths.Contains(p.SourcePath)).ToList();

        var written = accepted.Count == 0
            ? 0
            : await store.UpsertRawPointsAsync(runId, sourceEntityId, accepted, ct).ConfigureAwait(false);

        return new SaveOutcome(written, unitChange);
    }

    /// <summary>
    /// Точки сторінки без тих, що попередня сторінка вже віддала (межа ≥).
    /// </summary>
    /// <param name="points">Сторінка в порядку джерела.</param>
    /// <param name="carried">Скільки точок з кожною міткою ≥ курсора вже прочитано.</param>
    /// <remarks>
    /// ⚠ Відкидається рівно стільки перших точок із міткою, скільки їх уже
    /// прочитано, — не «всі з цією міткою»: точки з однаковою міткою, що не
    /// влізли в попередній батч, ідуть після прочитаних і мають лишитися.
    /// </remarks>
    private static IReadOnlyList<SourceDataPoint> Fresh(
        IReadOnlyList<SourceDataPoint> points, Dictionary<DateTime, int> carried)
    {
        if (carried.Count == 0)
        {
            return points;
        }

        var left = new Dictionary<DateTime, int>(carried);
        var fresh = new List<SourceDataPoint>(points.Count);

        foreach (var point in points)
        {
            if (left.TryGetValue(point.Timestamp, out var skip) && skip > 0)
            {
                left[point.Timestamp] = skip - 1;
                continue;
            }

            fresh.Add(point);
        }

        return fresh;
    }

    /// <summary>Скільки точок із кожною міткою ≥ <paramref name="cursor"/> є на сторінці.</summary>
    /// <remarks>
    /// Рахується за СИРОЮ сторінкою, разом із відкинутими дублями: вони теж
    /// лежать у ≥ курсора і наступна сторінка поверне їх знову.
    /// </remarks>
    private static Dictionary<DateTime, int> Carried(IReadOnlyList<SourceDataPoint> points, DateTime cursor)
    {
        var carried = new Dictionary<DateTime, int>();

        foreach (var point in points)
        {
            if (point.Timestamp >= cursor)
            {
                carried[point.Timestamp] = carried.GetValueOrDefault(point.Timestamp) + 1;
            }
        }

        return carried;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Підсумок збереження батча.</summary>
    /// <param name="Written">Скільки точок записано.</param>
    /// <param name="UnitChange">Причина-конверт про зміну одиниці; <c>null</c> — не було.</param>
    private sealed record SaveOutcome(int Written, JobProgressMessageEnvelope? UnitChange);

    /// <summary>Атрибути, які читаємо для сутності.</summary>
    /// <remarks>
    /// Мапінги полів задають, ЩО саме читати. Якщо їх немає, читається сама
    /// сутність — це нормальний випадок для одноатрибутного тега.
    /// </remarks>
    private static List<string> Paths(SourceEntity entity, IReadOnlyList<EntityFieldMap> maps)
        => maps.Count > 0
            ? maps.Select(m => m.SourceField).ToList()
            : [entity.EntityPath ?? entity.Code];

    private static int Percent(int done, int total)
        => total <= 0 ? 100 : Math.Clamp(done * 100 / total, 0, 99);

    /// <summary>Джерело недоступне з однієї з трьох причин — кожна свій <c>messageKey</c>.</summary>
    /// <param name="message">Запасне речення сервера (журнал; резолвер підміняє його клієнту).</param>
    /// <param name="sourceEntityId">Сутність джерела, що запустила прогін.</param>
    /// <param name="messageKey">Ключ каталогу — той самий факт незалежно від того, ЩО саме недоступне.</param>
    /// <param name="extra">Додаткова підстановка (`dataSourceId`/`transport`) — сирим рядком.</param>
    private static BusinessRuleException Unavailable(
        string message, int sourceEntityId, string messageKey, (string Key, string Value)? extra = null)
    {
        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["messageKey"] = messageKey,
            ["sourceEntityId"] = sourceEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        if (extra is { } pair)
        {
            details[pair.Key] = pair.Value;
        }

        return new(SourceUnavailable, message, details);
    }

    /// <summary>Результат однієї спроби читання.</summary>
    /// <param name="Collected">Прочитане; <c>null</c> — джерело відмовило.</param>
    /// <param name="ErrorCode">Код відмови; <c>null</c> — відмови не було.</param>
    /// <param name="Message">Текст відмови — без стека (ФВ-6.11).</param>
    /// <param name="Unauthorized">
    /// <c>true</c> — джерело відмовило в автентифікації (<c>H-20</c>): такий
    /// збій не йде в наздоганяння і не повторюється.
    /// </param>
    /// <remarks>
    /// ⚠ Поле зветься <c>Collected</c>, а не <c>Result</c>, свідомо:
    /// архітектурне правило 5 забороняє блокувальні <c>.Result</c> і шукає їх
    /// текстом. Властивість із такою назвою робила б правило шумним — а
    /// правило, яке звикли гасити винятками, перестає ловити справжні випадки.
    /// </remarks>
    private sealed record ReadOutcome(
        CollectionResult? Collected,
        string? ErrorCode,
        string? Message,
        bool Unauthorized = false);
}
