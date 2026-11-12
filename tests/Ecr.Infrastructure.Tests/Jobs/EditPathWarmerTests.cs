// tests/Ecr.Infrastructure.Tests/Jobs/EditPathWarmerTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-190: прогрів шляху першої правки (<see cref="EditPathWarmer"/>, <see cref="EditPathWarmupService"/>) — гріє рівно
/// метадані й граф формул версій, які дало джерело, у межах стелі; збій версії не зупиняє проходу; скасування
/// зупиняє; вимкнений ключ — нічого не робить; Quartz проганяє порожню задачу, лише коли планувальник запущено.
/// </summary>
/// <remarks>
/// ⛔ Провайдер scope містить ЛИШЕ кеш метаданих і сховище версій: будь-яка інша залежність прогріву (доступ,
/// значення комірок) не резолвилась би, і версія потрапила б у збої — тести це бачать.
/// Мутації (detached-worktree, див. опис коміту): прибрати <c>.Take(cap)</c> у циклі <c>WarmAsync</c>; прибрати
/// <c>catch</c> версії; прибрати перевірку <c>ReadEnabled</c> у <c>StartAsync</c>; прибрати перевірку <c>IsStarted</c>
/// у <c>WarmQuartzAsync</c>; прибрати фільтр <c>p.State IN (...)</c> у <c>OpenPeriodVersionsAsync</c>.
/// </remarks>
public sealed class EditPathWarmerTests : IAsyncLifetime
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private readonly IMetadataCache metadata = Substitute.For<IMetadataCache>();
    private readonly ITemplateVersionStore versions = Substitute.For<ITemplateVersionStore>();
    private readonly List<int> metadataCalls = [];
    private readonly List<int> dependencyCalls = [];
    private ServiceProvider? provider;
    private IScheduler? quartz;

    public EditPathWarmerTests()
    {
        metadata.GetAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (metadataCalls)
                {
                    metadataCalls.Add(call.Arg<int>());
                }

                return Task.FromResult<TemplateVersionSnapshot>(null!);
            });
        versions.ListFormulaDependenciesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (dependencyCalls)
                {
                    dependencyCalls.Add(call.Arg<int>());
                }

                return Task.FromResult<IReadOnlyList<FormulaDependency>>([]);
            });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (quartz is not null)
        {
            await quartz.Shutdown(waitForJobsToComplete: false);
        }

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Гріє_метадані_й_граф_кожної_версії_джерела_у_межах_стелі()
    {
        var askedCap = 0;
        var warmer = Warmer((_, cap, _) =>
        {
            askedCap = cap;
            return Task.FromResult<IReadOnlyList<int>>([50, 40, 30]);
        });

        var result = await warmer.WarmAsync(2, quartz: null, CancellationToken.None);

        Assert.Equal(2, askedCap);
        Assert.Equal([50, 40], result.WarmedVersions);
        Assert.Equal(0, result.FailedVersions);
        Assert.Null(result.QuartzMs);
        Assert.Equal([50, 40], metadataCalls);
        Assert.Equal([50, 40], dependencyCalls);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100_000, EditPathWarmer.MaxVersionsLimit)]
    public async Task Стеля_обмежена_від_одиниці_до_межі(int requested, int expected)
    {
        var askedCap = 0;
        var warmer = Warmer((_, cap, _) =>
        {
            askedCap = cap;
            return Task.FromResult<IReadOnlyList<int>>([]);
        });

        _ = await warmer.WarmAsync(requested, quartz: null, CancellationToken.None);

        Assert.Equal(expected, askedCap);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Збій_версії_йде_в_журнал_і_прохід_триває()
    {
        metadata.GetAsync(50, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("зламана версія"));
        var warmer = Warmer((_, _, _) => Task.FromResult<IReadOnlyList<int>>([50, 40]));

        var result = await warmer.WarmAsync(10, quartz: null, CancellationToken.None);

        Assert.Equal([40], result.WarmedVersions);
        Assert.Equal(1, result.FailedVersions);
        Assert.Equal([40], dependencyCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Збій_переліку_версій_не_кидає_і_нічого_не_гріє()
    {
        var warmer = Warmer((_, _, _) => throw new InvalidOperationException("база недоступна"));

        var result = await warmer.WarmAsync(10, quartz: null, CancellationToken.None);

        Assert.Empty(result.WarmedVersions);
        Assert.Empty(metadataCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Скасування_зупиняє_прохід_до_наступної_версії()
    {
        using var stop = new CancellationTokenSource();
        metadata.GetAsync(50, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                stop.Cancel();
                return Task.FromResult<TemplateVersionSnapshot>(null!);
            });
        var warmer = Warmer((_, _, _) => Task.FromResult<IReadOnlyList<int>>([50, 40]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warmer.WarmAsync(10, quartz: null, stop.Token));

        Assert.DoesNotContain(40, metadataCalls);
        Assert.DoesNotContain(40, dependencyCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Quartz_проганяє_одну_порожню_задачу_і_не_лишає_її_в_планувальнику()
    {
        var factory = await QuartzAsync(start: true);
        var warmer = Warmer((_, _, _) => Task.FromResult<IReadOnlyList<int>>([]));

        var result = await warmer.WarmAsync(10, factory, CancellationToken.None).WaitAsync(Safety);

        Assert.NotNull(result.QuartzMs);

        // Недурабельна: після прогону Quartz прибирає її сам (і триґер теж). Сигнал задача дає ВСЕРЕДИНІ Execute,
        // а прибирання йде одразу після нього — тому чекаємо стану (запобіжник Safety), а не міряємо час.
        using var deadline = new CancellationTokenSource(Safety);
        while ((await quartz!.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(EditPathWarmer.QuartzGroup))).Count > 0)
        {
            await Task.Delay(20, deadline.Token);
        }

        Assert.Empty(await quartz.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(EditPathWarmer.QuartzGroup)));
        Assert.Empty(await quartz.GetTriggerKeys(GroupMatcher<TriggerKey>.GroupEquals(EditPathWarmer.QuartzGroup)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Незапущений_Quartz_пропускається_після_обмеженого_очікування_старту_без_винятку()
    {
        var factory = await QuartzAsync(start: false);
        var warmer = new EditPathWarmer(Scopes(), NullLogger<EditPathWarmer>.Instance) { QuartzStartWait = TimeSpan.FromMilliseconds(300) };

        // Таймаут задачі більший за запобіжник тесту: якби прогрів поставив задачу, яку ніхто не виконає, тест упав би.
        var warmed = await warmer.WarmQuartzAsync(factory, TimeSpan.FromMinutes(5), CancellationToken.None).WaitAsync(Safety);

        Assert.False(warmed);
        Assert.Empty(await quartz!.GetJobKeys(GroupMatcher<JobKey>.GroupEquals(EditPathWarmer.QuartzGroup)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Quartz_що_стартує_після_ApplicationStarted_все_одно_прогрівається()
    {
        // Так у застосунку: QuartzHostedService запускає планувальник після ApplicationStarted, де стартує й прогрів.
        var factory = await QuartzAsync(start: false);
        var warmer = new EditPathWarmer(Scopes(), NullLogger<EditPathWarmer>.Instance);

        var warming = warmer.WarmQuartzAsync(factory, Safety, CancellationToken.None);
        await Task.Delay(200);
        await quartz!.Start();

        Assert.True(await warming.WaitAsync(Safety));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Служба_гріє_після_ApplicationStarted_і_зупиняється_разом_із_хостом()
    {
        using var lifetime = new TestLifetime();
        var service = Service(lifetime, new EditPathWarmupOptions(Enabled: true, MaxVersions: 5, WarmQuartz: false), [7]);

        await service.StartAsync(CancellationToken.None);
        Assert.Null(service.Completion);

        lifetime.NotifyStarted();
        await service.Completion!.WaitAsync(Safety);

        Assert.Equal([7], metadataCalls);
        Assert.Equal([7], dependencyCalls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Вимкнений_ключ_не_запускає_прогріву()
    {
        using var lifetime = new TestLifetime();
        var service = Service(lifetime, new EditPathWarmupOptions(Enabled: false, MaxVersions: 5, WarmQuartz: false), [7]);

        await service.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.Null(service.Completion);
        Assert.Empty(metadataCalls);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Зупинка_хоста_посеред_прогріву_скасовує_прохід_без_винятку()
    {
        metadata.GetAsync(7, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return (TemplateVersionSnapshot)null!;
            });
        using var lifetime = new TestLifetime();
        var service = Service(lifetime, new EditPathWarmupOptions(Enabled: true, MaxVersions: 5, WarmQuartz: false), [7, 6]);

        await service.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();
        await service.StopAsync(CancellationToken.None).WaitAsync(Safety);

        Assert.True(service.Completion!.IsCompletedSuccessfully);
        Assert.DoesNotContain(6, metadataCalls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public void Ключі_читаються_з_дефолтами_й_межами()
    {
        var empty = new ConfigurationBuilder().Build();
        Assert.True(EditPathWarmer.ReadEnabled(empty));
        Assert.Equal(EditPathWarmer.DefaultMaxVersions, EditPathWarmer.ReadMaxVersions(empty));

        var set = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EditPathWarmer.EnabledKey] = "false",
                [EditPathWarmer.MaxVersionsKey] = "100000",
            })
            .Build();
        Assert.False(EditPathWarmer.ReadEnabled(set));
        Assert.Equal(EditPathWarmer.MaxVersionsLimit, EditPathWarmer.ReadMaxVersions(set));
    }

    private EditPathWarmupService Service(TestLifetime lifetime, EditPathWarmupOptions options, IReadOnlyList<int> list)
    {
        var services = Services();

        // Джерело версій без бази: служба створює прогрів через CreateWarmer, а підмінене джерело читає перелік із
        // провайдера scope — тобто проходить тим самим шляхом, що й OpenPeriodVersionsAsync у застосунку.
        services.AddSingleton(new VersionSource(list));
        provider = services.BuildServiceProvider();
        return new EditPathWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(), lifetime, options,
            NullLogger<EditPathWarmupService>.Instance, NullLogger<EditPathWarmer>.Instance)
        {
            CreateWarmer = (scopes, log) => new EditPathWarmer(scopes, log)
            {
                ListVersions = (sp, _, _) => Task.FromResult(sp.GetRequiredService<VersionSource>().Versions),
            },
        };
    }

    private EditPathWarmer Warmer(Func<IServiceProvider, int, CancellationToken, Task<IReadOnlyList<int>>> list)
    {
        provider = Services().BuildServiceProvider();
        return new EditPathWarmer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<EditPathWarmer>.Instance)
        {
            ListVersions = list,
        };
    }

    private IServiceScopeFactory Scopes()
    {
        provider = Services().BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }

    private ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => metadata);
        services.AddScoped(_ => versions);
        return services;
    }

    private async Task<ISchedulerFactory> QuartzAsync(bool start)
    {
        var factory = new StdSchedulerFactory(new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-warm-tests-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        quartz = await factory.GetScheduler();
        if (start)
        {
            await quartz.Start();
        }

        return factory;
    }

    private sealed record VersionSource(IReadOnlyList<int> Versions);

    /// <summary>Життєвий цикл хоста без хоста: старт і зупинку тест сигналізує сам.</summary>
    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource started = new();
        private readonly CancellationTokenSource stopping = new();
        private readonly CancellationTokenSource stopped = new();

        public CancellationToken ApplicationStarted => started.Token;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => stopped.Token;

        public void NotifyStarted() => started.Cancel();

        public void StopApplication() => stopping.Cancel();

        public void Dispose()
        {
            started.Dispose();
            stopping.Dispose();
            stopped.Dispose();
        }
    }
}
