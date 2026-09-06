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
    ICollectionStore store) : ICollectionRunner
{
    /// <summary>Стеля точок на один запит до джерела.</summary>
    /// <remarks>
    /// PI AF на надмірний запит відповідає деградацією **всім** клієнтам,
    /// зокрема тим, що не наші. Батч обмежений з поваги до чужих клієнтів, а
    /// не з любові до сторінкування.
    /// </remarks>
    public const int MaxPointsPerRequest = 5_000;

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
                         sourceEntityId);

        var dataSource = await store.FindDataSourceAsync(entity.DataSourceId, ct).ConfigureAwait(false)
                         ?? throw Unavailable(
                             $"Джерело {entity.DataSourceId} не існує або вимкнене.", sourceEntityId);

        // ⚠ Транспорт — це НАЛАШТУВАННЯ, а не гілка коду (ФВ-11.2). Адаптер
        // обирається за Transport джерела; додати третій транспорт означає
        // зареєструвати ще одну реалізацію, а не правити цей метод.
        var adapter = sources.FirstOrDefault(s => s.Transport == dataSource.Transport)
                      ?? throw Unavailable(
                          $"Транспорт {dataSource.Transport} не зареєстровано.", sourceEntityId);

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

        try
        {
            foreach (var interval in work)
            {
                var complete = true;

                foreach (var path in paths)
                {
                    var outcome = await ReadAsync(
                        adapter, dataSource.Id, sourceEntityId, path, interval, ct).ConfigureAwait(false);

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
                                adapter, dataSource.Id, sourceEntityId, path, interval, ct)
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
                        retrieved += await SaveAsync(
                            runId, sourceEntityId, result.Points, maps, units, ct).ConfigureAwait(false);

                        if (result.ErrorCode is null && result.FailedIntervals.Count == 0)
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
            // Зміна одиниці джерела зупиняє збір (ФВ-16.9): те, що вже
            // прочитано, лишається, покриття за незавершені інтервали — ні.
            await store
                .WriteCoverageAsync(runId, sourceEntityId, covered, CancellationToken.None)
                .ConfigureAwait(false);

            await store
                .FinishRunAsync(runId, "Failed", retrieved, ex.Message, CancellationToken.None)
                .ConfigureAwait(false);

            throw;
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
                ["sourceEntityId"] = sourceEntityId,
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

    /// <summary>Перевіряє одиниці й зберігає точки.</summary>
    private async Task<int> SaveAsync(
        long runId,
        int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points,
        IReadOnlyList<EntityFieldMap> maps,
        UnitCatalogSnapshot units,
        CancellationToken ct)
    {
        if (points.Count == 0)
        {
            return 0;
        }

        foreach (var point in points)
        {
            var map = maps.FirstOrDefault(
                m => string.Equals(m.SourceField, point.SourcePath, StringComparison.OrdinalIgnoreCase));

            SourceUnitConverter.EnsureDeclaredUnit(
                map?.SourceUnitId, point.SourceUnitSymbol, units, point.SourcePath);
        }

        return await store
            .UpsertRawPointsAsync(runId, sourceEntityId, points, ct)
            .ConfigureAwait(false);
    }

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

    private static BusinessRuleException Unavailable(string message, int sourceEntityId)
        => new(
            SourceUnavailable,
            message,
            new Dictionary<string, object?> { ["sourceEntityId"] = sourceEntityId });

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
