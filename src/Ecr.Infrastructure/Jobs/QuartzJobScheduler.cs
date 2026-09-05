using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Quartz;

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
    IJobProgressStore? progress = null) : IBackgroundJobScheduler
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

    /// <summary>Чи піднято планувальник.</summary>
    public bool IsConfigured => schedulerFactory is not null;

    /// <inheritdoc />
    public async Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct)
        where TJob : IBackgroundJob
    {
        var scheduler = Scheduler(typeof(TJob).Name);

        // ⚠ Ключ унікальний на постановку, а не на тип задачі: два перерахунки
        // різних документів — це дві задачі, і спільний ключ зробив би другу
        // «вже запланованою».
        var jobId = $"{typeof(TJob).Name}-{Guid.NewGuid():N}";

        var detail = JobBuilder.Create<QuartzJobAdapter>()
            .WithIdentity(jobId)
            .UsingJobData(PayloadKey, JsonSerializer.Serialize(payload, PayloadOptions))
            .UsingJobData(JobCodeKey, typeof(TJob).FullName ?? typeof(TJob).Name)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"{jobId}-trigger")
            .StartNow()
            .Build();

        var instance = await scheduler.GetScheduler(ct).ConfigureAwait(false);
        await instance.ScheduleJob(detail, trigger, ct).ConfigureAwait(false);

        return jobId;
    }

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
