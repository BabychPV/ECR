using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Quartz;
using Quartz.Impl.Matchers;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Реалізація <see cref="IBackgroundJobScheduler"/> на Quartz (Apache-2.0).
/// </summary>
/// <remarks>
/// Порт існує саме для того, щоб заміна на Hangfire коштувала день, якщо ІБ
/// погодить LGPL (D-09). Тому специфіка Quartz не має протікати назовні.
///
/// ⚠ <paramref name="schedulerFactory"/> необов'язковий, і це не зручність.
/// Поки планувальник не піднято, реалізація має **існувати і бути
/// зареєстрованою**: без неї не резолвиться <c>RecalculateDocumentHandler</c>,
/// без нього не створюється <c>DocumentsController</c>, і <c>500</c>
/// отримують ВСІ його ендпоінти — включно з поданням, яке черги не потребує
/// взагалі. Одна відсутня реєстрація вимикала цілий контролер, і побачити це
/// можна було лише на живому запиті.
///
/// Тому без фабрики методи черги відмовляють зрозуміло — <c>ECR-SYS-0503</c>, —
/// а все, що черги не потребує, працює.
/// </remarks>
public sealed class QuartzJobScheduler(
    ISchedulerFactory? schedulerFactory = null,
    IJobProgressStore? progress = null,
    Ecr.Domain.Abstractions.IClock? clock = null,
    ICorrelationIdAccessor? correlation = null) : IBackgroundJobScheduler
{
    private const string UnavailableCode = "ECR-SYS-0503";

    private const string UnavailableMessage =
        "Фонові задачі ще не налаштовані: планувальник не піднято (D-09). " +
        "Операція потребує черги і тому недоступна.";

    /// <summary>Ключ, під яким payload лежить у <c>JobDataMap</c>.</summary>
    /// <remarks>
    /// Payload кладеться **рядком JSON**, а не об'єктом: Quartz серіалізує
    /// <c>JobDataMap</c> у сховище, і довільний тип там або не серіалізується
    /// зовсім, або серіалізується так, що після оновлення збірки не читається.
    /// </remarks>
    public const string PayloadKey = "ecr.payload";

    /// <summary>Ключ коду задачі — за ним прогрес зіставляється з типом.</summary>
    public const string JobCodeKey = "ecr.jobCode";

    /// <summary>Мітка часу постановки в чергу, у тиках UTC (<c>ФВ-12.2</c>).</summary>
    public const string EnqueuedAtKey = "ecr.enqueuedAtTicks";

    /// <summary>
    /// Ключ лічильника спроб — у <c>JobDataMap</c> ТРИҐЕРА, не задачі (D-134,
    /// №11 T10 #40).
    /// </summary>
    /// <remarks>
    /// ⚠ Саме триґера. <see cref="QuartzJobAdapter"/> не позначений
    /// <c>[PersistJobDataAfterExecution]</c> — зміна <c>JobDetail.JobDataMap</c>
    /// усередині <c>Execute</c> ніде не зберігається, і наступний прогін читав
    /// би той самий «0» знову й знову. Дані триґера, навпаки, задаються ПРИ
    /// ЙОГО СТВОРЕННІ — новий триґер на ретрай несе вже інкрементоване
    /// значення.
    /// </remarks>
    public const string RetryAttemptKey = "ecr.retryAttempt";

    /// <summary>
    /// Ключ ознаки "поставлено через <see cref="ScheduleAsync{TJob}"/>" —
    /// за ним <see cref="QuartzJobAdapter"/> вирішує, чи брати
    /// міжінстансовий лок (Q-223, `Jobs`-секція).
    /// </summary>
    /// <remarks>
    /// ⚠ Саме тут, а не прапорцем від викликача: `ScheduleAsync` — ЄДИНИЙ
    /// метод цього класу з крон-виразом у сигнатурі, тобто єдиний, що
    /// реєструє job окремо на КОЖНОМУ інстансі під тим самим детермінованим
    /// ключем. `EnqueueAsync`/`EnqueueExclusiveAsync` ставлять job з
    /// унікальним (GUID) ключем у ВЛАСНИЙ `in-memory`-планувальник
    /// інстансу, що його викликав, — інші інстанси про неї не знають
    /// узагалі, тож координація там не потрібна.
    /// </remarks>
    public const string RecurringKey = "ecr.recurring";

    /// <summary>
    /// Скільки тіл задач злиття, що впали остаточно, тримає процес для ручного перезапуску
    /// (огляд O1, косметика) — далі найстаріше забувається.
    /// </summary>
    public const int MaxFailedMergeBodies = 256;

    /// <inheritdoc />
    /// <remarks>⚠ Черга в пам'яті: постановка — лише ПІСЛЯ коміту викликача (MI-02 (в)).</remarks>
    public bool EnlistsInCallerTransaction => false;

    /// <summary>Чи поставлена задача через <see cref="ScheduleAsync{TJob}"/> (крон).</summary>
    /// <param name="detail">Деталь задачі; <c>null</c> — задачі немає.</param>
    /// <remarks>
    /// ⚠ Одне визначення на адаптер і на скасування: розбіжність означала б,
    /// що одне місце вважає задачу розкладом, а інше — разовою і видаляє її.
    /// </remarks>
    public static bool IsRecurring(IJobDetail? detail)
        => detail is not null
           && detail.JobDataMap.ContainsKey(RecurringKey)
           && detail.JobDataMap.GetString(RecurringKey) == "1";

    /// <summary>Ім'я міжінстансового локу задачі (Q-223).</summary>
    /// <param name="jobId">Ідентифікатор (ім'я ключа) задачі.</param>
    public static string LockNameOf(string jobId) => $"Ecr.Job.{jobId}";

    /// <summary>
    /// Ім'я локу, який бере <see cref="QuartzJobAdapter"/> на тик розкладу
    /// <typeparamref name="TJob"/> з цим payload.
    /// </summary>
    /// <remarks>
    /// ⛔ Для викликів ПОЗА планувальником (стартове вирівнювання станів
    /// періодів): лок з іншою назвою не конкурував би з погодинним прогоном
    /// тієї самої задачі, і обидва бігли б паралельно.
    /// </remarks>
    public static string RecurringLockName<TJob>(object? payload)
        where TJob : IBackgroundJob
        => LockNameOf(RecurringJob<TJob>(payload).Key.Name);

    /// <summary>
    /// Ключ кореляції (BE-08): у даних задачі — від постановки, у даних
    /// триґера — від ручного перезапуску чи ретраю; триґер важить більше.
    /// </summary>
    public const string CorrelationKey = "ecr.correlationId";

    /// <summary>Кореляція запиту, що ставить задачу, або нова — поза запитом.</summary>
    private string NewCorrelationId()
        => correlation?.CorrelationId is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N");

    /// <summary>Чи піднято планувальник.</summary>
    public bool IsConfigured => schedulerFactory is not null;

    /// <inheritdoc />
    public async Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        // ⚠ Ключ унікальний на постановку, а не на тип задачі: два перерахунки
        // різних документів — це дві задачі, і спільний ключ зробив би другу
        // «вже запланованою».
        return await EnqueueCoreAsync<TJob>(
            instance, $"{typeof(TJob).Name}-{Guid.NewGuid():N}", payload, ct, createdByUserId)
            .ConfigureAwait(false);
    }

    /// <summary>Спільна частина постановки: запис прогресу і планування.</summary>
    /// <typeparam name="TJob">Маркер задачі.</typeparam>
    /// <param name="instance">Планувальник.</param>
    /// <param name="jobId">Готовий ідентифікатор задачі.</param>
    /// <param name="payload">Завдання.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="createdByUserId">Хто поставив задачу; <c>null</c> — системна (Q-156).</param>
    /// <returns>Ідентифікатор задачі.</returns>
    /// <remarks>
    /// ⚠ Виділено тому, що постановок стало дві — звичайна і з витісненням
    /// (<c>H-23c</c>). Різняться вони лише тим, як складається ідентифікатор;
    /// друга копія решти рядків рано чи пізно забула б запис прогресу, і
    /// клієнт отримав би <c>404</c> на задачу, яку щойно прийняли.
    /// </remarks>
    private async Task<string> EnqueueCoreAsync<TJob>(
        IScheduler instance, string jobId, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        var correlationId = NewCorrelationId();
        var json = JsonSerializer.Serialize(payload, PayloadOptions);

        var builder = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(jobId)
            .UsingJobData(PayloadKey, json)
            .UsingJobData(JobCodeKey, typeof(TJob).FullName ?? typeof(TJob).Name)
            .UsingJobData(CorrelationKey, correlationId);

        // ⚠ Мітка постановки (`ФВ-12.2`). У базі момент постановки Є —
        // `QueueAsync` нижче створює рядок зі станом `Queued`, — але він НЕ
        // ПЕРЕЖИВАЄ старту: `JobProgress.Begin` перезаписує `UpdatedAt`, а
        // `StartedAt` ставить уже на мить запуску. Тобто з бази затримку можна
        // взяти рівно в мить переходу і ніколи після неї.
        //
        // ⚠ Мітка в JobDataMap — судження, не безвихідь: так замір не додає
        // походу до СУБД на шлях, який сам і міряється, і не залежить від
        // сховища прогресу, яке для адаптера необов'язкове.
        //
        // ⛔ Без годинника мітки НЕМА, і запасного `DateTime.UtcNow` тут бути
        // не може: `ForbiddenApiTests` забороняє системний час поза
        // реалізацією `IClock`, і небезпідставно — замір, що бере час із двох
        // різних джерел, порівнював би непорівнюване. Немає годинника —
        // затримка просто не міряється (`QuartzJobAdapter` це передбачає).
        if (clock is not null)
        {
            builder = builder.UsingJobData(EnqueuedAtKey, clock.UtcNow.Ticks);
        }

        var detail = builder
            // ⚠ Дурабельна навмисно (D-134, №11 T10 #40/#50): задача, що
            // вичерпала ретраї, мусить пережити свій єдиний триґер, інакше
            // ручний перезапуск не мав би чого перезапускати. Успіх і
            // скасування прибирають деталь самі (QuartzJobAdapter.Execute) —
            // держати НАЗАВЖДИ лишається тільки те, що впало остаточно.
            .StoreDurably()
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{jobId}-trigger")
            .StartNow()
            .Build();

        // ⚠ Запис прогресу створюється ДО постановки, а не при старті задачі.
        // Клієнт отримує 202 з jobId і одразу починає опитувати стан; без
        // цього рядка він отримав би 404 на задачу, яку щойно прийняли, і
        // вирішив би, що вона загубилася.
        if (progress is not null && clock is not null)
        {
            await progress
                .QueueAsync(
                    jobId, typeof(TJob).FullName ?? typeof(TJob).Name, clock.UtcNow, ct, createdByUserId,
                    correlationId, DocumentIdOf(json))
                .ConfigureAwait(false);
        }

        // ⚠ Слухач злиття — ДО постановки: він має побачити старт кожної разової задачі,
        // інакше злиття з нею під час виконання не знало б, що вона жива.
        CoalescedRequeueListener.Of(instance);

        await instance.ScheduleJob(detail, trigger, ct).ConfigureAwait(false);

        return jobId;
    }

    /// <summary>Документ задачі — числова властивість <c>documentId</c> кореня payload (BE-08).</summary>
    /// <remarks>
    /// ⚠ Судження: з payload, а не новим параметром порту. Документні задачі
    /// (експорт, імпорт, перерахунок документа) вже несуть <c>DocumentId</c>
    /// у тілі, а параметр змусив би кожного викликача дублювати те саме число.
    /// </remarks>
    private static long? DocumentIdOf(string json)
    {
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("documentId", out var id)
               && id.ValueKind == JsonValueKind.Number
               && id.TryGetInt64(out var value)
            ? value
            : null;
    }

    /// <inheritdoc />
    public async Task<string> EnqueueExclusiveAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        // ⚠ Ціль входить у ключ, і саме тому попередню задачу над тією самою
        // ціллю можна знайти, нічого про неї не пам'ятаючи. Ідентифікатор
        // лишається унікальним (хвіст із GUID): якби ключ був сталим, запис
        // прогресу нової задачі затер би стан скасованої, і в журналі не
        // лишилося б сліду, що вона взагалі була.
        var prefix = TargetPrefixOf<TJob>(targetKey);

        // ⛔ Витіснення ПЕРЕД постановкою. У зворотному порядку між двома
        // прогонами існував би проміжок, у якому працюють обидва, — а вони
        // пишуть в одні й ті самі результати.
        var superseded = await instance
            .GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct)
            .ConfigureAwait(false);

        foreach (var key in superseded)
        {
            if (key.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                await CancelAsync(key.Name, ct).ConfigureAwait(false);
            }
        }

        return await EnqueueCoreAsync<TJob>(
            instance, prefix + Guid.NewGuid().ToString("N"), payload, ct, createdByUserId)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Quartz у пам'яті не має бар'єра «Queued позаду Running», як черга в базі:
    /// поставлена поруч задача пішла б ПАРАЛЕЛЬНО з виконуваною й писала б в ті самі
    /// результати. Тому: є на ціль задача, що чекає (має триґер) або виконується в
    /// ЦЬОМУ планувальнику, — нічого не ставимо й нікого не перериваємо, повертаємо
    /// її ідентифікатор (задача, що чекає, має перевагу над виконуваною). Інакше —
    /// постановка з тим самим ключем, що в <see cref="EnqueueExclusiveAsync{TJob}"/>,
    /// але без витіснення.
    /// <para>
    /// ⛔ Злиття з ВИКОНУВАНОЮ задачею позначає її «брудною»
    /// (<see cref="CoalescedRequeueListener"/>): вона могла прочитати дані ДО нової
    /// зміни, тож коли завершиться будь-як (успіх, провал, скасування), задача на
    /// ціль ставиться ще раз — один раз на скільки завгодно злиттів, з payload
    /// ОСТАННЬОЇ постановки. Без цього свіжі дані матеріалізації лишалися б
    /// непорахованими до випадкової наступної постановки, а Quartz — типовий режим.
    /// Перед ретраєм позначка лишається: вона діє на завершення ретраю.
    /// </para>
    /// <para>
    /// ⚠ Дурабельна деталь задачі, що впала остаточно (без триґера й не
    /// виконується), злиттю не заважає — її вже ніхто не виконає.
    /// </para>
    /// <para>
    /// ⛔ Між інстансами застосунку дедупу немає — як і для Exclusive: черга кожного
    /// інстанса в його пам'яті, і задачу на ту саму ціль в іншому інстансі звідси
    /// не видно. Міжінстансова коалесценція — лише в режимі <c>Database</c>.
    /// </para>
    /// </remarks>
    public async Task<string> EnqueueCoalescedAsync<TJob>(
        string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetKey);

        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        if (JobPayloadMerge.ArrayPathOf(typeof(TJob).FullName) is { } mergePath)
        {
            return await MergeAsync<TJob>(instance, TargetPrefixOf<TJob>(targetKey), mergePath, payload, createdByUserId, ct)
                .ConfigureAwait(false);
        }

        return await CoalesceAsync<TJob>(
                instance, TargetPrefixOf<TJob>(targetKey), payload, createdByUserId, finishedJobId: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Злиття з об'єднанням масиву payload (O1, <see cref="JobPayloadMerge"/>).</summary>
    /// <remarks>
    /// ⚠ Масив дописується в задачу, що ЧЕКАЄ (ще не взяла payload у
    /// <see cref="QuartzJobAdapter"/>, <see cref="QuartzPayloadMerges.Take"/>); та, що
    /// вже виконується, злиття не приймає — поруч ставиться нова. Паралельно з
    /// виконуваною вона однаково не рахує: лок документа (<see cref="RecalculationDocumentLock"/>).
    /// </remarks>
    private async Task<string> MergeAsync<TJob>(
        IScheduler instance, string prefix, string mergePath, object? payload, int? createdByUserId, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        var json = JsonSerializer.Serialize(payload, PayloadOptions);
        var (jobId, created) = QuartzPayloadMerges.MergeOrOpen(
            instance.SchedulerName, prefix, mergePath, json, prefix + Guid.NewGuid().ToString("N"));

        if (!created)
        {
            return jobId;
        }

        try
        {
            return await EnqueueCoreAsync<TJob>(instance, jobId, payload, ct, createdByUserId).ConfigureAwait(false);
        }
        catch
        {
            QuartzPayloadMerges.Forget(jobId);
            throw;
        }
    }

    /// <summary>
    /// Перепостановка комірок задачі злиття, що вичерпала стелю відкладень (Д-1 огляду O1):
    /// та сама ціль, що в <paramref name="failedJobId"/>, звичайним злиттям — у задачу цілі,
    /// що чекає, або новою.
    /// </summary>
    /// <param name="instance">Планувальник задачі, що впала.</param>
    /// <param name="failedJobId">Задача злиття (<c>префікс цілі + GUID</c>).</param>
    /// <param name="payloadJson">Тіло перепостановки (<see cref="JobDeferral.RequeuePayload"/>).</param>
    /// <param name="createdByUserId">Автор задачі, що впала.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Задача, що виконає комірки.</returns>
    internal async Task<string> RequeueDeferralExhaustedAsync(
        IScheduler instance, string failedJobId, string payloadJson, int? createdByUserId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        // Хвіст ідентифікатора задачі злиття — Guid "N" (MergeAsync).
        const int GuidLength = 32;
        if (failedJobId is not { Length: > GuidLength })
        {
            throw new ArgumentException("Не ідентифікатор задачі злиття.", nameof(failedJobId));
        }

        using var payload = JsonDocument.Parse(payloadJson);

        return await MergeAsync<IFormulaRecalculationJob>(
                instance, failedJobId[..^GuidLength], FormulaRecalculationTarget.MergedArrayPath,
                payload.RootElement.Clone(), createdByUserId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Злиття на ціль; <paramref name="finishedJobId"/> — задача, що щойно завершилась (не рахується).</summary>
    private async Task<string> CoalesceAsync<TJob>(
        IScheduler instance, string prefix, object? payload, int? createdByUserId, string? finishedJobId,
        CancellationToken ct)
        where TJob : IBackgroundJob
    {
        var listener = CoalescedRequeueListener.Of(instance);

        var executing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var context in await instance.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false))
        {
            executing.Add(context.JobDetail.Key.Name);
        }

        string? running = null;

        foreach (var key in await instance.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct).ConfigureAwait(false))
        {
            if (!key.Name.StartsWith(prefix, StringComparison.Ordinal)
                || string.Equals(key.Name, finishedJobId, StringComparison.Ordinal))
            {
                continue;
            }

            if (executing.Contains(key.Name))
            {
                running ??= key.Name;
            }
            else if ((await instance.GetTriggersOfJob(key, ct).ConfigureAwait(false)).Count > 0)
            {
                return key.Name;
            }
        }

        // ⚠ Виконувана, чий ключ уже зник зі сховища (не-дурабельна), теж рахується.
        running ??= executing.FirstOrDefault(name => name.StartsWith(prefix, StringComparison.Ordinal)
                                                     && !string.Equals(name, finishedJobId, StringComparison.Ordinal));

        if (running is null)
        {
            return await EnqueueCoreAsync<TJob>(
                    instance, prefix + Guid.NewGuid().ToString("N"), payload, ct, createdByUserId)
                .ConfigureAwait(false);
        }

        Task<string> Requeue(string finished, CancellationToken token)
            => CoalesceAsync<TJob>(instance, prefix, payload, createdByUserId, finished, token);

        // ⚠ Слухач бачить лише задачі, що стартували ПІСЛЯ його реєстрації (а реєструється
        // він на кожній постановці). «Не жива» при виконуваній — стартувала раніше: беремо
        // під нагляд, якщо вона ще виконується; інакше вона вже завершилась, і позначку
        // ніхто б не зняв — ставимо самі.
        if (listener.MarkDirty(running, Requeue, adoptRunning: false))
        {
            return running;
        }

        var stillExecuting = (await instance.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false))
            .Any(c => string.Equals(c.JobDetail.Key.Name, running, StringComparison.Ordinal));

        return stillExecuting && listener.MarkDirty(running, Requeue, adoptRunning: true)
            ? running
            : await CoalesceAsync<TJob>(instance, prefix, payload, createdByUserId, running, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Слухач Quartz, що перепоставляє задачу на ціль після завершення «брудної»
    /// виконуваної — див. <see cref="EnqueueCoalescedAsync{TJob}"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ Слухач, а не <see cref="QuartzJobAdapter"/>: адаптер створюється фабрикою задач
    /// і не бачить стану планувальника без нової DI-реєстрації, а слухач живе рівно
    /// там, де й черга, — у пам'яті цього <see cref="IScheduler"/>. І він чує
    /// завершення будь-якої гілки адаптера (успіх, провал, скасування), бо Quartz кличе
    /// <see cref="JobWasExecuted"/> після кожного прогону.
    /// </remarks>
    private sealed class CoalescedRequeueListener : IJobListener
    {
        private const string ListenerName = "ecr.coalesced-requeue";

        private static readonly Lock Registration = new();

        private readonly Lock gate = new();

        /// <summary>Задачі, що виконуються зараз (від старту до завершення прогону).</summary>
        private readonly HashSet<string> live = new(StringComparer.Ordinal);

        /// <summary>«Брудні» виконувані: перепостановка з payload останнього злиття.</summary>
        private readonly Dictionary<string, Func<string, CancellationToken, Task<string>>> dirty =
            new(StringComparer.Ordinal);

        public string Name => ListenerName;

        /// <summary>Слухач цього планувальника — один на екземпляр, реєструється за першої потреби.</summary>
        public static CoalescedRequeueListener Of(IScheduler instance)
        {
            lock (Registration)
            {
                // ⚠ GetJobListener(name) на відсутньому кидає KeyNotFoundException — шукаємо в переліку.
                if (instance.ListenerManager.GetJobListeners()
                        .OfType<CoalescedRequeueListener>()
                        .FirstOrDefault() is { } existing)
                {
                    return existing;
                }

                var created = new CoalescedRequeueListener();
                instance.ListenerManager.AddJobListener(created, EverythingMatcher<JobKey>.AllJobs());

                return created;
            }
        }

        /// <summary>Позначає виконувану задачу брудною; <c>false</c> — слухач її не бачить живою.</summary>
        public bool MarkDirty(string jobId, Func<string, CancellationToken, Task<string>> requeue, bool adoptRunning)
        {
            lock (gate)
            {
                if (!live.Contains(jobId))
                {
                    if (!adoptRunning)
                    {
                        return false;
                    }

                    live.Add(jobId);
                }

                // Кілька злиттів за прогін — одна перепостановка, з останнім payload.
                dirty[jobId] = requeue;
                return true;
            }
        }

        public Task JobToBeExecuted(IJobExecutionContext context, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                live.Add(context.JobDetail.Key.Name);
            }

            return Task.CompletedTask;
        }

        public Task JobExecutionVetoed(IJobExecutionContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task JobWasExecuted(
            IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
        {
            var jobId = context.JobDetail.Key.Name;

            try
            {
                // ⚠ Ретрай (новий триґер тієї самої задачі) — ще не завершення: позначка
                // лишається до кінця ретраю, а в паузі ретраю злиття бере саму задачу як ту, що чекає.
                var retryPending = (await context.Scheduler
                        .GetTriggersOfJob(context.JobDetail.Key, CancellationToken.None)
                        .ConfigureAwait(false))
                    .Any(t => !t.Key.Equals(context.Trigger.Key));

                Func<string, CancellationToken, Task<string>>? requeue = null;

                lock (gate)
                {
                    live.Remove(jobId);

                    if (!retryPending && dirty.Remove(jobId, out var pending))
                    {
                        requeue = pending;
                    }
                }

                if (requeue is not null)
                {
                    await requeue(jobId, CancellationToken.None).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // ⛔ Виняток зі слухача Quartz зриває закриття триґера задачі.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                System.Diagnostics.Trace.TraceError(
                    "Перепостановка після злиття для задачі {0} не вдалася: {1}", jobId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Префікс ідентифікатора задачі на ціль — ОДИН для Exclusive і Coalesced,
    /// інакше вони не бачили б задач одне одного.
    /// </summary>
    private static string TargetPrefixOf<TJob>(string targetKey)
        where TJob : IBackgroundJob
        => $"{typeof(TJob).Name}{TargetSeparator}{Sanitize(targetKey)}{TargetSeparator}";

    /// <summary>Роздільник між типом задачі, ціллю і хвостом ідентифікатора.</summary>
    /// <remarks>
    /// ⚠ Не дефіс: дефіс уже вживається всередині <c>Guid</c>-подібних хвостів
    /// і в кодах цілей, і пошук за префіксом ловив би зайве.
    ///
    /// ⛔ І НЕ <c>#</c>, як тут стояло. Ідентифікатор задачі — це СЕГМЕНТ
    /// ШЛЯХУ в <c>GET /api/v1/jobs/{jobId}</c>, а <c>#</c> в URL починає
    /// фрагмент: усе після нього до сервера не доїжджає взагалі. Клієнт
    /// застосунку рятував себе сам (<c>encodeURIComponent</c> у всіх дев'яти
    /// місцях), і тому з інтерфейсу дефект був невидимий — але будь-який
    /// інший споживач отримував <c>404</c> на щойно прийняту задачу.
    ///
    /// ⚠ Це не здогад. <c>tools/smoke.ps1</c> — єдина перевірка продукту «як
    /// у користувача» — падала на кроці 17 саме цим:
    /// <c>GET /api/v1/jobs/IRecalculationJob#doc1-p202609#46fec230…</c>
    /// повернув <c>404</c>, очікувалося 200. Шістнадцять кроків до нього
    /// проходили.
    ///
    /// ⚠ <c>~</c> обраний тому, що він <b>unreserved</b> за RFC 3986, тобто в
    /// сегменті шляху не потребує кодування взагалі, і водночас неможливий у
    /// коді цілі (<c>EcrCode</c> — латиниця, цифри, підкреслення) та в
    /// <c>Guid("N")</c>. Кодування (<c>%23</c>) полагодило б рівно тих
    /// клієнтів, які й так не ламалися, лишивши ідентифікатор ворожим до
    /// решти.
    /// </remarks>
    private const char TargetSeparator = '~';

    /// <summary>Прибирає з цілі символи, які ламають пошук за префіксом.</summary>
    private static string Sanitize(string targetKey)
        => targetKey.Replace(TargetSeparator, '_');

    /// <inheritdoc />
    public async Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        // ⛔ ДО звернення до Quartz: той кинув би FormatException уже після
        // DeleteJob — тобто невалідна правка знімала б чинний розклад.
        if (!IsValidCron(cronExpression, out var cronError))
        {
            throw new ArgumentException(
                $"Невалідний cron-вираз «{cronExpression}»: {cronError}", nameof(cronExpression));
        }

        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        var (key, json) = RecurringJob<TJob>(payload);
        await instance.DeleteJob(key, ct).ConfigureAwait(false);

        var detail = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(key)
            .UsingJobData(PayloadKey, json)
            .UsingJobData(JobCodeKey, typeof(TJob).FullName ?? typeof(TJob).Name)
            .UsingJobData(RecurringKey, "1")
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{key.Name}-trigger")
            .WithCronSchedule(cronExpression)
            .Build();

        await instance.ScheduleJob(detail, trigger, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        // DeleteJob прибирає й тригери задачі; false — задачі не було.
        return await instance.DeleteJob(RecurringJob<TJob>(payload).Key, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    bool IBackgroundJobScheduler.IsValidCron(string expression, out string? error)
        => IsValidCron(expression, out error);

    /// <summary>Перевіряє cron-вираз Quartz (6–7 полів, напр. <c>0 15 2 * * ?</c>).</summary>
    /// <param name="expression">Вираз.</param>
    /// <param name="error">Текст помилки розбору; <c>null</c>, коли вираз валідний.</param>
    public static bool IsValidCron(string expression, out string? error)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "вираз порожній";
            return false;
        }

        try
        {
            CronExpression.ValidateExpression(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Ключ і тіло періодичної задачі — ОДНЕ місце для постановки і зняття.</summary>
    /// <remarks>
    /// ⛔ Два окремі обчислення розійшлися б мовчки: зняття рахувало б інший
    /// ключ, повертало <c>false</c>, а «вимкнений» розклад збирав би далі.
    /// </remarks>
    private static (JobKey Key, string Json) RecurringJob<TJob>(object? payload)
        where TJob : IBackgroundJob
    {
        var json = JsonSerializer.Serialize(payload, PayloadOptions);

        // ⚠ Ключ СТАЛИЙ — ім'я типу плюс відбиток payload. Сталість робить
        // розклад ідемпотентним: повторний старт застосунку не плодить
        // дванадцять копій нічної перевірки, які всі прокинуться об одній
        // годині.
        //
        // ⚠ Payload входить у ключ, і це не деталь: збір за розкладом
        // ставиться ОКРЕМО на кожну сутність джерела, і спільний ключ на тип
        // лишив би одну задачу з останнім payload — решта джерел мовчки
        // ніколи не збиралася б.
        //
        // ⛔ Відбиток — SHA-256, а не GetHashCode: той рандомізований на
        // кожен запуск процесу, і «сталий» ключ мінявся б при кожному
        // рестарті, накопичуючи задачі-двійники.
        return (new JobKey($"{typeof(TJob).Name}:{Fingerprint(json)}"), json);
    }

    /// <inheritdoc />
    public async Task CancelAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var scheduler = Scheduler(jobId);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        // ⚠ Задачі, що вже виконується, надсилається СКАСУВАННЯ, а не
        // переривання потоку: убитий посеред пакета перерахунок лишив би
        // половину результатів записаними, і жоден статус про це не сказав би.
        var executingHere = false;

        foreach (var executing in await instance.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false))
        {
            if (string.Equals(executing.JobDetail.Key.Name, jobId, StringComparison.Ordinal))
            {
                executingHere = true;
                await instance.Interrupt(executing.JobDetail.Key, ct).ConfigureAwait(false);
            }
        }

        // ⛔ Розклад НЕ знімається: скасування через API — прохання зупинити
        // поточний прогін, а прогрес розкладу пишеться під його ключем, тож
        // погодинну задачу в стані Running видно в `/jobs` і її можна скасувати.
        // DeleteJob тут зняв би крон-тригер до рестарту. Зняття розкладу —
        // окремий метод (UnscheduleAsync).
        var jobKey = new JobKey(jobId);
        if (!IsRecurring(await instance.GetJobDetail(jobKey, ct).ConfigureAwait(false)))
        {
            await instance.DeleteJob(jobKey, ct).ConfigureAwait(false);

            // Знята з черги задача злиття більше не приймає масивів — наступна постановка стане новою.
            if (!executingHere)
            {
                QuartzPayloadMerges.Forget(jobId);
            }
        }

        // ⛔ U3: стан пишеться ТУТ, а не лише адаптером. Адаптер ставить
        // `Cancelled` тільки задачі, що ВИКОНУЄТЬСЯ (гілка
        // `OperationCanceledException`); задача в черзі чи в паузі ретраю
        // (`Running` між спробами) просто зникала з планувальника, а рядок
        // лишався `Queued`/`Running` НАЗАВЖДИ — і для перевірки узгодженості
        // ще й блокував кнопку запуску (409, `RunConsistencyCheckHandler`).
        //
        // ⚠ Запис умовний (лише активний рядок):
        // - задача в черзі чи в паузі ретраю цього інстанса — щойно знята, це
        //   єдиний, хто запише її стан;
        // - задача ІНШОГО інстанса (його черга в пам'яті, звідси недосяжна) —
        //   цей рядок і є сигналом: її биття побачить неактивний рядок і
        //   скасує задачу, а постановка з черги не стартує (`QuartzJobAdapter`);
        // - розклад, чий тик іде деінде, — те саме; сам розклад лишається.
        //
        // ⚠ Задачу, що виконується ТУТ, не чіпаємо: вона ще добігає пакет, і
        // `Cancelled` запише адаптер у мить, коли справді зупиниться. Ранній
        // запис показав би «скасовано» роботі, яка ще пише результати.
        if (!executingHere && progress is not null && clock is not null)
        {
            await progress.CancelActiveAsync(jobId, clock.UtcNow, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Підтверджує в <c>itg.JobProgress</c>, що задачі з локальної черги
    /// цього процесу ще живі (U4).
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки активних рядків підтверджено.</returns>
    /// <remarks>
    /// ⛔ Без цього періодичне прибирання позначало б <c>Failed</c> задачі, які
    /// просто довго чекають вільного потоку чи ретраю: у черзі биття не б'є
    /// ніхто. Розклади сюди не входять — їхній рядок б'є лише прогін, що йде.
    /// </remarks>
    public async Task<int> KeepAliveLocalJobsAsync(CancellationToken ct)
    {
        if (schedulerFactory is null || progress is null || clock is null)
        {
            return 0;
        }

        var instance = await schedulerFactory.GetScheduler(ct).ConfigureAwait(false);
        if (instance.IsShutdown)
        {
            return 0;
        }

        var held = new List<string>();

        foreach (var key in await instance.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct).ConfigureAwait(false))
        {
            if (!IsRecurring(await instance.GetJobDetail(key, ct).ConfigureAwait(false)))
            {
                held.Add(key.Name);
            }
        }

        return held.Count == 0
            ? 0
            : await progress.KeepAliveAsync(held, clock.UtcNow, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Надсилає скасування КОЖНІЙ задачі, що зараз виконується в цьому процесі (U8).
    /// </summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Скільки задач отримали сигнал.</returns>
    /// <remarks>
    /// ⛔ Для зупинки хоста. <c>WaitForJobsToComplete</c> лише ЧЕКАЄ задачі, а
    /// сигналу їм не дає (<c>interruptJobsOnShutdown</c> у Quartz за
    /// замовчуванням вимкнено): довгий перерахунок обривався разом із процесом
    /// після <c>ShutdownTimeout</c>, і рядок лишався <c>Running</c>. Зі
    /// скасуванням задача встигає вийти через свою гілку
    /// <c>OperationCanceledException</c> і записати <c>Cancelled</c>.
    /// </remarks>
    public async Task<int> InterruptAllAsync(CancellationToken ct)
    {
        if (schedulerFactory is null)
        {
            return 0;
        }

        var instance = await schedulerFactory.GetScheduler(ct).ConfigureAwait(false);
        if (instance.IsShutdown)
        {
            return 0;
        }

        var interrupted = 0;

        foreach (var executing in await instance.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false))
        {
            if (await instance.Interrupt(executing.JobDetail.Key, ct).ConfigureAwait(false))
            {
                interrupted++;
            }
        }

        return interrupted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Планується НОВИЙ одноразовий триґер на ТОЙ САМИЙ <c>JobKey</c>, а не
    /// новий запис черги: клієнт, що вже показує <c>jobId</c> провальної
    /// задачі, має побачити той самий ідентифікатор знову «у черзі», а не
    /// отримати другий, про який нічого не знає.
    /// <para>
    /// Лічильник ретраїв скидається в нуль явно (<see cref="RetryAttemptKey"/>
    /// у даних нового триґера): ручний перезапуск — це нова спроба людини, а
    /// не продовження вичерпаної автоматичної серії.
    /// </para>
    /// </remarks>
    public async Task<bool> RestartAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var scheduler = Scheduler(jobId);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

        var jobKey = new JobKey(jobId);
        if (!await instance.CheckExists(jobKey, ct).ConfigureAwait(false))
        {
            return false;
        }

        var trigger = TriggerBuilder.Create()
            .ForJob(jobKey)
            .WithIdentity($"{jobId}-restart-{Guid.NewGuid():N}-trigger")
            .UsingJobData(RetryAttemptKey, "0")
            .UsingJobData(CorrelationKey, NewCorrelationId())
            .StartNow()
            .Build();

        await instance.ScheduleJob(trigger, ct).ConfigureAwait(false);

        if (progress is not null && clock is not null)
        {
            await progress.RestartAsync(jobId, clock.UtcNow, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Читає з <c>itg.JobProgress</c>, а не з внутрішнього стану Quartz.
    /// Інстансів застосунку кілька, і клієнт, що опитує прогрес, потрапляє не
    /// обов'язково на той, який задачу виконує: стан у пам'яті планувальника
    /// відповів би «немає такої».
    /// <para>
    /// Єдиний метод, що без планувальника **не кидає**: питання «як там
    /// задача» має отримати відповідь, а не помилку — інакше екран прогресу
    /// ламається на порожньому місці.
    /// </para>
    /// </remarks>
    public async Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        if (progress is null)
        {
            return new JobStatus(jobId, "Unavailable", 0, UnavailableMessage, UnavailableCode);
        }

        return await progress.FindAsync(jobId, ct).ConfigureAwait(false)
               ?? new JobStatus(jobId, "Unknown", 0, null, null);
    }

    /// <inheritdoc />
    public async Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return progress is null
            ? null
            : await progress.GetCreatedByUserIdAsync(jobId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(
        JobListFilter filter, int limit, CancellationToken ct)
        => progress is null
            ? []
            : await progress.ListRecentAsync(filter, limit, ct).ConfigureAwait(false);

    /// <summary>Налаштування серіалізації payload; спільні на всі виклики.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions PayloadOptions =
        new(JsonSerializerDefaults.Web);

    /// <summary>Скільки символів відбитка входить у ключ.</summary>
    /// <remarks>
    /// Шістнадцять шістнадцяткових символів — це 64 біти. Колізія на десятках
    /// розкладів неможлива практично, а повний хеш зробив би ключ довшим за
    /// обмеження імені задачі й нечитабельним у журналі.
    /// </remarks>
    private const int FingerprintLength = 16;

    /// <summary>Сталий відбиток payload.</summary>
    private static string Fingerprint(string json)
        => System.Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(json)))[..FingerprintLength];

    /// <summary>Фабрика планувальника або зрозуміла відмова.</summary>
    private ISchedulerFactory Scheduler(string what)
        => schedulerFactory
           ?? throw new BusinessRuleException(
               UnavailableCode, UnavailableMessage, new Dictionary<string, object?>
               {
                   ["messageKey"] = "err.ECR-SYS-0503.schedulerNotConfigured",
                   ["job"] = what,
               });
}

/// <summary>
/// Payload задач злиття Quartz (O1, <see cref="JobPayloadMerge"/>): актуальне тіло
/// задачі живе тут, а не в <c>JobDataMap</c>, доки задачу не взяв адаптер.
/// </summary>
/// <remarks>
/// ⛔ Межа злиття — <see cref="Take"/> під тим самим замком, що й злиття: або масив
/// потрапив у тіло ДО старту задачі (і вона його порахує), або задача вже стартувала
/// й відчеплена від цілі — і нова постановка стає новою задачею. Перевірка «чи є в
/// задачі триґер» такої межі не дає: триґер спрацьовує незалежно від неї.
/// <para>
/// ⚠ Статичне сховище, ключ — ім'я планувальника й ціль: <c>JobId</c> із GUID
/// унікальний між планувальниками, а черга Quartz однаково в пам'яті процесу.
/// </para>
/// </remarks>
internal static class QuartzPayloadMerges
{
    private static readonly Lock Gate = new();

    private static readonly Dictionary<string, Entry> ByJob = new(StringComparer.Ordinal);

    /// <summary>Ціль → задача, що приймає злиття (щонайбільше одна).</summary>
    private static readonly Dictionary<string, string> OpenByTarget = new(StringComparer.Ordinal);

    /// <summary>Тіла задач, що впали остаточно, від найстарішого (<see cref="MarkFailed"/>).</summary>
    private static readonly LinkedList<string> FailedOrder = new();

    private static readonly Dictionary<string, LinkedListNode<string>> FailedById = new(StringComparer.Ordinal);

    /// <summary>
    /// Задача злиття впала остаточно: тіло лишається для ручного перезапуску, але не вічно —
    /// понад <see cref="QuartzJobScheduler.MaxFailedMergeBodies"/> найстаріше забувається.
    /// </summary>
    /// <remarks>
    /// ⚠ Без межі сховище росло б на кожну Failed задачу формул до рестарту процесу (огляд O1,
    /// косметика). Забуте тіло не ламає перезапуск: адаптер бере payload першої постановки
    /// з <c>JobDataMap</c>, а комірки вичерпаної стелі вже перепоставлено (Д-1).
    /// </remarks>
    public static void MarkFailed(string jobId)
    {
        lock (Gate)
        {
            if (!ByJob.ContainsKey(jobId) || FailedById.ContainsKey(jobId))
            {
                return;
            }

            FailedById[jobId] = FailedOrder.AddLast(jobId);

            while (FailedOrder.Count > QuartzJobScheduler.MaxFailedMergeBodies)
            {
                var oldest = FailedOrder.First!.Value;
                ForgetLocked(oldest);
            }
        }
    }

    /// <summary>Зливає масив у задачу цілі, що чекає, або реєструє <paramref name="newJobId"/>.</summary>
    /// <returns>Задача, що виконає роботу; <c>Created</c> — її треба поставити в Quartz.</returns>
    public static (string JobId, bool Created) MergeOrOpen(
        string scope, string prefix, string path, string json, string newJobId)
    {
        var target = $"{scope}|{prefix}";

        lock (Gate)
        {
            if (OpenByTarget.TryGetValue(target, out var openId)
                && ByJob.TryGetValue(openId, out var open)
                && !open.Taken)
            {
                if (JobPayloadMerge.Merge(open.Payload, json, path, out var fresh) is { } merged)
                {
                    open.Payload = merged;
                    open.Fresh |= fresh;
                    return (openId, false);
                }

                // Не вміщається — наявна лишається як є, ціль переходить до нової.
                OpenByTarget.Remove(target);
            }

            ByJob[newJobId] = new Entry(target) { Payload = json };
            OpenByTarget[target] = newJobId;
            return (newJobId, true);
        }
    }

    /// <summary>Адаптер бере тіло задачі на старті; далі злиття в неї не йде.</summary>
    /// <returns><c>null</c> — задача не злиття (тіло — у <c>JobDataMap</c>).</returns>
    /// <param name="jobId">Задача.</param>
    /// <param name="fresh">
    /// Від попереднього взяття в тіло злито НОВІ комірки (Д-1 огляду O1): відлік стелі
    /// відкладень, що живе в триґері, для цього прогону починається заново.
    /// </param>
    public static string? Take(string jobId, out bool fresh)
    {
        lock (Gate)
        {
            fresh = false;
            if (!ByJob.TryGetValue(jobId, out var entry))
            {
                return null;
            }

            fresh = entry.Fresh;
            entry.Fresh = false;
            entry.Taken = true;
            Revive(jobId);
            if (OpenByTarget.TryGetValue(entry.Target, out var open) && string.Equals(open, jobId, StringComparison.Ordinal))
            {
                OpenByTarget.Remove(entry.Target);
            }

            return entry.Payload;
        }
    }

    /// <summary>Задача знову чекає (відкладення, ретрай): приймає злиття, якщо ціль вільна.</summary>
    public static void Reopen(string jobId)
    {
        lock (Gate)
        {
            if (ByJob.TryGetValue(jobId, out var entry) && !OpenByTarget.ContainsKey(entry.Target))
            {
                entry.Taken = false;
                OpenByTarget[entry.Target] = jobId;
            }
        }
    }

    /// <summary>Задача завершилась або знята — тіло більше не потрібне.</summary>
    public static void Forget(string jobId)
    {
        lock (Gate)
        {
            ForgetLocked(jobId);
        }
    }

    /// <summary>Знімає позначку «впала остаточно» (перезапуск: задача знову жива).</summary>
    private static void Revive(string jobId)
    {
        if (FailedById.Remove(jobId, out var node))
        {
            FailedOrder.Remove(node);
        }
    }

    private static void ForgetLocked(string jobId)
    {
        Revive(jobId);

        if (ByJob.Remove(jobId, out var entry)
            && OpenByTarget.TryGetValue(entry.Target, out var open)
            && string.Equals(open, jobId, StringComparison.Ordinal))
        {
            OpenByTarget.Remove(entry.Target);
        }
    }

    private sealed class Entry(string target)
    {
        public string Target { get; } = target;

        public required string Payload { get; set; }

        public bool Taken { get; set; }

        /// <summary>Злиття додало нові комірки після останнього <see cref="Take"/>.</summary>
        public bool Fresh { get; set; }
    }
}
