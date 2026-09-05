using Ecr.Application.Errors;
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

        try
        {
            foreach (var interval in work)
            {
                var complete = true;

                foreach (var path in paths)
                {
                    var outcome = await ReadAsync(
                        adapter, dataSource.Id, sourceEntityId, path, interval, ct).ConfigureAwait(false);

                    if (outcome.Collected is { } result)
                    {
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
            return new ReadOutcome(await adapter.ReadAsync(request, ct).ConfigureAwait(false), null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
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
    /// <remarks>
    /// ⚠ Поле зветься <c>Collected</c>, а не <c>Result</c>, свідомо:
    /// архітектурне правило 5 забороняє блокувальні <c>.Result</c> і шукає їх
    /// текстом. Властивість із такою назвою робила б правило шумним — а
    /// правило, яке звикли гасити винятками, перестає ловити справжні випадки.
    /// </remarks>
    private sealed record ReadOutcome(CollectionResult? Collected, string? ErrorCode, string? Message);
}
