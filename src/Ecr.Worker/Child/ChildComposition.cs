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
/// <c>ChildCompositionTests</c>. <c>ICorrelationIdAccessor</c> не реєструється
/// навмисно: його споживачі беруть його необов'язково, а кореляцію задачі воркер бере з
/// рядка черги. ✎ 2026-10-01: <c>IJobStartMetrics</c> Api-адаптера (над <c>EcrMetrics</c>)
/// тут і далі немає — веб-частина; натомість <see cref="ChildTelemetry"/> дає власний
/// адаптер у тому ж Meter <c>Ecr</c> і OTLP-експорт метрик дочірнього (раніше вони
/// емітились у нікуди).
/// </remarks>
internal static class ChildComposition
{
    /// <summary>
    /// Мітка <c>mode</c> вимірів бюджету перерахунку в дочірньому процесі пулу (L2-12): дочірній
    /// існує лише в режимі черги <c>Database</c>, тож і мітка — завжди вона.
    /// </summary>
    public const string BudgetMode = nameof(JobQueueMode.Database);

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
            MaxDuration = pool.MaxDuration,

            // L2-03: зависла задача — вихід, наглядач перезапустить слот.
            OnHang = () => Environment.Exit(JobWorkerOptions.ExitJobHung),
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

        // Метрики процесу задачі (ecr.job.failed, start_latency, кеші): без цього вони
        // емітились у нікуди — див. ChildTelemetry.
        services.AddChildTelemetry(configuration);

        // ⛔ L2-12: мітка mode у вимірах бюджету ПРД-13 (ecr.calc.full_year) — не з конфігурації:
        // у дочірнього вона читалась із файлу Api («Quartz»), хоча дочірній існує лише в режимі
        // Database (deploy-ecr.ps1 пише ECR_Jobs__Queue__Mode лише в оточення EcrApi).
        foreach (var budget in services.Where(d => d.ServiceType == typeof(RecalculationBudgetOptions)).ToList())
        {
            services.Remove(budget);
        }

        services.AddSingleton(RecalculationBudgetOptions.Read(configuration) with { Mode = BudgetMode });

        services.AddSingleton(WorkerOptions(pool));
        services.AddHostedService<JobWorker>();
    }
}
