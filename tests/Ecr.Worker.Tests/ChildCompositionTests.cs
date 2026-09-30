// tests/Ecr.Worker.Tests/ChildCompositionTests.cs

using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Ecr.Worker.Child;
using Ecr.Worker.Isolation;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, I1): складання дочірнього воркера — лише лейн перерахунку,
/// роль <c>wrk</c>, одна задача на процес, без фонових служб Api і без Data Protection.
/// </summary>
/// <remarks>
/// Контейнер будується без бази: створення <c>EcrDbContext</c> з'єднання не відкриває.
/// Мутаційні докази — в описі коміту.
/// </remarks>
public sealed class ChildCompositionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Виконавець_дочірнього_бере_лише_recalc_з_роллю_wrk_по_одній_задачі()
    {
        var options = ChildComposition.WorkerOptions(new WorkerPoolOptions { MaxDuration = TimeSpan.FromMinutes(7) });

        Assert.Equal([JobLanes.Recalc], options.Lanes);
        Assert.Equal(JobProgressStore.RoleWorker, options.Role);
        Assert.Equal(1, options.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMinutes(7), options.MaxDuration);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData("Database", "InProcess")]
    [InlineData("Database", "Worker")]
    [InlineData("Quartz", null)]
    public void Дочірній_має_один_виконавець_черги_і_жодної_фонової_служби_Api(string mode, string? executor)
    {
        var services = Compose(mode, executor);

        // ⛔ Quartz і воркер з лейнами Api (ApiLanes) тут означали б, що дочірній
        // бере default або вдруге крутить розклади — по разу на процес пулу.
        var hosted = Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(JobWorker), hosted.ImplementationType);

        var options = Assert.Single(services, d => d.ServiceType == typeof(JobWorkerOptions));
        Assert.Equal([JobLanes.Recalc], ((JobWorkerOptions)options.ImplementationInstance!).Lanes);

        var scheduler = Assert.Single(services, d => d.ServiceType == typeof(IBackgroundJobScheduler));
        Assert.Equal(typeof(DbBackgroundJobScheduler), scheduler.ImplementationType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public async Task Граф_задачі_перерахунку_резолвиться_без_Data_Protection()
    {
        var services = Compose("Database", "Worker");

        // Передумова доказу: Data Protection у дочірньому не зареєстровано взагалі.
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IDataProtectionProvider));

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        // ⛔ Усе, що задача перерахунку бере з контейнера: сама задача, обробник
        // завершення (fencing), і те, що оркестратор резолвить у своїх scope на пакет.
        Assert.IsType<RecalculationJob>(sp.GetRequiredService<IRecalculationJob>());
        Assert.NotNull(sp.GetRequiredService<MethodologyResolver>());
        Assert.NotNull(sp.GetRequiredService<CalculationInputBuilder>());
        Assert.NotNull(sp.GetRequiredService<CalculationOutputWriter>());
        Assert.NotEmpty(sp.GetRequiredService<IEnumerable<ICalculationModule>>());
        Assert.NotNull(sp.GetRequiredService<IJobQueue>());
        Assert.NotNull(sp.GetRequiredService<IJobProgressStore>());
        Assert.NotNull(sp.GetRequiredService<JobLeaseContext>());

        // Задача без автора — анонім, як у Api поза запитом: права відмовляють, а не пропускають.
        var user = sp.GetRequiredService<ICurrentUser>();
        Assert.IsType<JobAwareCurrentUser>(user);
        Assert.Null(user.UserId);
        Assert.Empty(user.GroupSids);

        Assert.IsType<JobWorker>(Assert.Single(provider.GetServices<IHostedService>()));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-9.8")]
    public void Журнал_дочірнього_не_пише_SQL_команди_EF_на_Information()
    {
        // Той самий шлях конфігурації, що в WorkerProgram.CreateBuilder: тека exe
        // як корінь, worker.settings.json поверх. appsettings.json Api у теці
        // тестів немає — рівень мусить дати саме файл воркера (I2-2: 173 МБ
        // журналу за прогін, коли його не було).
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")));
        builder.Configuration.AddJsonFile(WorkerProgram.SettingsFile, optional: false, reloadOnChange: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Ecr"] = "Server=.;Database=EcrChildCompositionProbe;Integrated Security=true",
            [DbBackgroundJobScheduler.ModeKey] = "Database",
            [JobLaneMap.ExecutorKey] = "Worker",
        });
        ChildComposition.AddChildWorker(builder.Services, builder.Configuration, new WorkerPoolOptions());

        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();

        var command = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
        Assert.False(command.IsEnabled(LogLevel.Information));
        Assert.True(command.IsEnabled(LogLevel.Warning));

        // Власний журнал задач лишається на Information.
        Assert.True(factory.CreateLogger<JobWorker>().IsEnabled(LogLevel.Information));
    }

    private static ServiceCollection Compose(string mode, string? executor)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ecr"] = "Server=.;Database=EcrChildCompositionProbe;Integrated Security=true",
                [DbBackgroundJobScheduler.ModeKey] = mode,
                [JobLaneMap.ExecutorKey] = executor,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        ChildComposition.AddChildWorker(services, configuration, new WorkerPoolOptions());
        return services;
    }
}
