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
    Ecr.Domain.Abstractions.IClock? clock = null) : IBackgroundJobScheduler
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
        var detail = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(jobId)
            .UsingJobData(PayloadKey, JsonSerializer.Serialize(payload, PayloadOptions))
            .UsingJobData(JobCodeKey, typeof(TJob).FullName ?? typeof(TJob).Name)
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
                .QueueAsync(jobId, typeof(TJob).FullName ?? typeof(TJob).Name, clock.UtcNow, ct, createdByUserId)
                .ConfigureAwait(false);
        }

        await instance.ScheduleJob(detail, trigger, ct).ConfigureAwait(false);

        return jobId;
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
        var prefix = $"{typeof(TJob).Name}{TargetSeparator}{Sanitize(targetKey)}{TargetSeparator}";

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

    /// <summary>Роздільник між типом задачі, ціллю і хвостом ідентифікатора.</summary>
    /// <remarks>
    /// ⚠ Не дефіс: дефіс уже вживається всередині <c>Guid</c>-подібних хвостів
    /// і в кодах цілей, і пошук за префіксом ловив би зайве.
    /// </remarks>
    private const char TargetSeparator = '#';

    /// <summary>Прибирає з цілі символи, які ламають пошук за префіксом.</summary>
    private static string Sanitize(string targetKey)
        => targetKey.Replace(TargetSeparator, '_');

    /// <inheritdoc />
    public async Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        var scheduler = Scheduler(typeof(TJob).Name);
        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);

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
        var key = new JobKey($"{typeof(TJob).Name}:{Fingerprint(json)}");
        await instance.DeleteJob(key, ct).ConfigureAwait(false);

        var detail = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(key)
            .UsingJobData(PayloadKey, json)
            .UsingJobData(JobCodeKey, typeof(TJob).FullName ?? typeof(TJob).Name)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{key.Name}-trigger")
            .WithCronSchedule(cronExpression)
            .Build();

        await instance.ScheduleJob(detail, trigger, ct).ConfigureAwait(false);
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
        foreach (var executing in await instance.GetCurrentlyExecutingJobs(ct).ConfigureAwait(false))
        {
            if (string.Equals(executing.JobDetail.Key.Name, jobId, StringComparison.Ordinal))
            {
                await instance.Interrupt(executing.JobDetail.Key, ct).ConfigureAwait(false);
            }
        }

        await instance.DeleteJob(new JobKey(jobId), ct).ConfigureAwait(false);
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
    public async Task<IReadOnlyList<JobSummary>> ListRecentAsync(int limit, CancellationToken ct)
        => progress is null
            ? []
            : await progress.ListRecentAsync(limit, ct).ConfigureAwait(false);

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
               UnavailableCode, UnavailableMessage, new Dictionary<string, object?> { ["job"] = what });
}
