// src/Ecr.Worker/Child/ChildComposition.cs

using Ecr.Application;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Worker.Isolation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecr.Worker.Child;

/// <summary>
/// Складання дочірнього воркера (<c>Ecr.Worker --child</c>, ФВ-9.8, D-206, I1):
/// той самий DI задач перерахунку, що в Api, і той самий <see cref="JobWorker"/>
/// — лише на лейні <see cref="JobLanes.Recalc"/>.
/// </summary>
/// <remarks>
/// ⚠ Складання Api не копіюється, а викликається: <c>AddEcrInfrastructure</c>,
/// <c>AddEcrCalculations</c>, <c>AddEcrApplication</c>, <c>AddEcrJobActor</c>.
/// Веб-частина (автентифікація, Data Protection, HTTP-кореляція, метрики Api)
/// сюди не йде: задачі перерахунку її не резолвлять — це доводить
/// <c>ChildCompositionTests</c>. <c>ICorrelationIdAccessor</c> і
/// <c>IJobStartMetrics</c> не реєструються навмисно: усі їхні споживачі беруть
/// їх необов'язково (<c>GetService</c> / параметр <c>= null</c>), а кореляцію
/// задачі воркер бере з рядка черги.
/// </remarks>
internal static class ChildComposition
{
    /// <summary>Налаштування виконавця черги дочірнього процесу.</summary>
    /// <param name="pool">Налаштування пулу (<c>Jobs:Workers:*</c>).</param>
    /// <remarks>
    /// ⛔ <c>MaxConcurrency = 1</c>: паралелізм пулу — КІЛЬКІСТЬ процесів
    /// (<see cref="WorkerPoolOptions.Count"/>), а межа пам'яті Job Object — на
    /// процес. Дві задачі в одному процесі ділили б одну межу, і падіння від
    /// пам'яті однієї вбивало б сусідню.
    /// </remarks>
    public static JobWorkerOptions WorkerOptions(WorkerPoolOptions pool)
    {
        ArgumentNullException.ThrowIfNull(pool);

        return new JobWorkerOptions
        {
            Lanes = [JobLanes.Recalc],
            Role = JobProgressStore.RoleWorker,
            MaxConcurrency = 1,
        };
    }

    /// <summary>Реєструє в <paramref name="services"/> усе для виконання задач перерахунку.</summary>
    /// <param name="services">Колекція сервісів хоста.</param>
    /// <param name="configuration">Конфігурація (оточення служби + <c>worker.settings.json</c>).</param>
    /// <param name="pool">Налаштування пулу.</param>
    public static void AddChildWorker(IServiceCollection services, IConfiguration configuration, WorkerPoolOptions pool)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var hostOwn = services.Where(d => d.ServiceType == typeof(IHostedService)).ToHashSet();

        services.AddEcrInfrastructure(configuration);
        services.AddEcrCalculations(configuration.GetSection(CalculationLimits.SectionName).Get<CalculationLimits>());
        services.AddEcrApplication();
        services.AddSingleton<NoRequestCurrentUser>();
        services.AddEcrJobActor<NoRequestCurrentUser>();

        // ⛔ Фонові служби складання Api тут не живуть: Quartz (розклади й разові
        // задачі режиму Quartz — справа Api) і виконавець черги з лейнами Api
        // (JobLaneMap.ApiLanes). Лишити їх — і дочірній брав би лейн default
        // або крутив би розклади вдруге, по одному разу на процес пулу.
        foreach (var descriptor in services
                     .Where(d => (d.ServiceType == typeof(IHostedService) && !hostOwn.Contains(d))
                                 || d.ServiceType == typeof(JobWorkerOptions)
                                 || d.ServiceType == typeof(IBackgroundJobScheduler))
                     .ToList())
        {
            services.Remove(descriptor);
        }

        // Задача перерахунку може поставити наступну — лише в чергу в базі:
        // Quartz у пам'яті цього процесу ніхто б не виконав.
        services.AddScoped<IBackgroundJobScheduler, DbBackgroundJobScheduler>();

        services.AddSingleton(WorkerOptions(pool));
        services.AddHostedService<JobWorker>();
    }
}
