using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;

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
public sealed class CollectionRunner(
    IEnumerable<IExternalDataSource> sources,
    SourceUnitConverter unitConverter,
    CatchUpPlanner catchUp,
    ICollectionStore store,
    TimeSpan? maxRunDuration = null) : ICollectionRunner
{
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
    /// поріг: `GetAsync` у гіршому разі — це ~96 с на одну пару
    /// інтервал/атрибут (30-секундний таймаут HttpClient (`Q-250`,
    /// `DependencyInjection.cs`) плюс паузи ретраю 2 с і 4 с), а
    /// `RunAsync` іде по інтервалах наздоганяння (до 45 діб, див.
    /// <see cref="CatchUpLookback"/>) і атрибутах ПОСЛІДОВНО — без стелі
    /// «напівживе» джерело (відповідає, але повільно) тримало б воркер
    /// Quartz годинами замість хвилин. П'ятнадцять хвилин дають запас на
    /// кілька десятків повільних пар, лишаючись далеко від «годин» із
    /// симптому.
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
        string? failureMessage = null;
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

        // ⚠ Годинник прогону (Q-250): рахує ЛИШЕ звідси, а не з початку
        // методу — підготовка вище (пошук сутності, мапінгів, планування)
        // у джерело не ходить і в цей ліміт не входить. Пов'язаний із
        // зовнішнім `ct`: скасування задачі ззовні (Quartz `Interrupt`)
        // скасовує й watchdog теж, і catch нижче навмисно відрізняє один
        // випадок від іншого за станом САМЕ зовнішнього токена.
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watchdog.CancelAfter(runDuration);
        var runToken = watchdog.Token;

        try
        {
            foreach (var interval in work)
            {
                var complete = true;

                foreach (var path in paths)
                {
                    // Мапінг, поставлений на паузу через зміну одиниці, у
                    // цьому прогоні більше не читається; інтервал лишається
                    // непокритим — після рішення людини його забере наздоганяння.
                    if (pausedPaths.Contains(path))
                    {
                        complete = false;
                        continue;
                    }

                    var outcome = await ReadAsync(
                        adapter, dataSource.Id, sourceEntityId, path, interval, runToken).ConfigureAwait(false);

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
                                adapter, dataSource.Id, sourceEntityId, path, interval, runToken)
                                .ConfigureAwait(false);
                        }

                        if (outcome.Unauthorized)
                        {
                            // ⛔ Прогін обривається ТУТ. Решта інтервалів і
                            // атрибутів не читається: ті самі облікові дані
                            // дадуть ту саму відмову, а прогін від цього стане
                            // лише довшим (`H-20`).
                            throw await FailAuthenticationAsync(
                                runId, sourceEntityId, entity.Code, covered, retrieved, outcome.Message)
                                .ConfigureAwait(false);
                        }
                    }

                    if (outcome.Collected is { } result)
                    {
                        accepted = true;

                        // ⚠ Успішні точки зберігаються НАВІТЬ при частковій
                        // відмові батча: викинути прочитане через те, що хвіст
                        // діапазону не дався, означало б читати його вдруге —
                        // і так до наступної відмови.
                        var saved = await SaveAsync(
                            runId, sourceEntityId, result.Points, maps, units, pausedPaths, ct).ConfigureAwait(false);
                        retrieved += saved.Written;

                        if (saved.UnitChange is { } change)
                        {
                            failureCode ??= SourceUnitConverter.UnitChangedCode;
                            failureMessage ??= change;
                        }
                        else if (result.ErrorCode is null && result.FailedIntervals.Count == 0)
                        {
                            continue;
                        }
                    }

                    complete = false;
                    failureCode ??= outcome.ErrorCode ?? SourceUnavailable;
                    failureMessage ??= outcome.Message;
                }

                // ⛔ Покриття пишеться ЛИШЕ за повністю прочитаний інтервал.
                // Записане наперед покриття — це дірка, яку більше ніхто не
                // знайде: наздоганяння шукає прогалини саме тут.
                if (complete)
                {
                    covered.Add(interval);
                }

                step++;
                await progress
                    .ReportAsync(
                        Percent(step, work.Count),
                        $"Зібрано точок: {retrieved}; інтервалів {step} із {work.Count}",
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
                .WriteCoverageAsync(runId, sourceEntityId, covered, CancellationToken.None)
                .ConfigureAwait(false);

            await store
                .FinishRunAsync(runId, "Failed", retrieved, ex.Message, CancellationToken.None)
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
            failureCode ??= SourceUnavailable;
            failureMessage ??=
                $"Прогін перевищив ліміт часу {runDuration.TotalMinutes:0} хв: джерело відповідає, "
                + "але надто повільно. Непрочитане піде в наздоганяння наступного разу.";
        }

        await store.WriteCoverageAsync(runId, sourceEntityId, covered, ct).ConfigureAwait(false);

        // ⚠ Відмова джерела — «Degraded», а не «Failed», і виняток НЕ
        // кидається: діапазон лишився непокритим, наздоганяння візьме його
        // наступного разу. Це затримка, а не збій.
        await store
            .FinishRunAsync(
                runId,
                failureCode is null ? "Succeeded" : "Degraded",
                retrieved,
                failureCode is null ? null : $"{failureCode}: {failureMessage ?? "джерело недоступне"}",
                ct)
            .ConfigureAwait(false);

        await progress
            .ReportAsync(
                100,
                failureCode is null
                    ? $"Збір завершено: {retrieved} точок"
                    : $"Збір завершено частково: {retrieved} точок, діапазон у наздоганянні",
                ct)
            .ConfigureAwait(false);
    }

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

        string? unitChange = null;

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
            unitChange ??= $"Атрибут «{map.SourceField}» повертає одиницю «{actualCode}», "
                           + $"а в мапінгу оголошено одиницю {map.SourceUnitId}. Мапінг призупинено.";
        }

        var accepted = pausedPaths.Count == 0
            ? points
            : points.Where(p => !pausedPaths.Contains(p.SourcePath)).ToList();

        var written = accepted.Count == 0
            ? 0
            : await store.UpsertRawPointsAsync(runId, sourceEntityId, accepted, ct).ConfigureAwait(false);

        return new SaveOutcome(written, unitChange);
    }

    /// <summary>Підсумок збереження батча.</summary>
    /// <param name="Written">Скільки точок записано.</param>
    /// <param name="UnitChange">Текст про зміну одиниці; <c>null</c> — не було.</param>
    private sealed record SaveOutcome(int Written, string? UnitChange);

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
