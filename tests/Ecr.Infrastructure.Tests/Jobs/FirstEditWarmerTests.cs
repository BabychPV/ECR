// tests/Ecr.Infrastructure.Tests/Jobs/FirstEditWarmerTests.cs
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.Core;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// AN-194: прогрів першого читання зрізу й першого <c>PATCH</c> (<see cref="FirstEditWarmer"/>) — іде у фоні після
/// <c>ApplicationStarted</c> і не тримає старту; вимикається ключем <see cref="FirstEditWarmer.EnabledKey"/> і не
/// вмикається в процесі, що зрізів і правок не обслуговує; кличе порти лише неіснуючими ідентифікаторами й нічого не пише.
/// </summary>
/// <remarks>
/// Без таймінгів: час прогріву — лише у звіті заміру AN-194, не в тестах CI.
/// Мутації (локальний worktree, відкат): прибрати <c>|| options.WarmFirstEdit</c> у <c>StartAsync</c> — червоний
/// «Служба_гріє…»; прибрати <c>servesEdits &amp;&amp;</c> у <c>EditPathWarmupOptions.Read</c> — «Ключ_і_процес…»; підставити
/// справжній ідентифікатор замість <c>NoId</c> у кроці рядків — «Типові_кроки…»; прибрати <c>catch</c> кроку —
/// «Збій_кроку…».
/// </remarks>
public sealed class FirstEditWarmerTests : IDisposable
{
    private static readonly TimeSpan Safety = TimeSpan.FromSeconds(30);

    private ServiceProvider? provider;

    public void Dispose() => provider?.Dispose();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Служба_гріє_першу_правку_у_фоні_після_старту_не_тримаючи_старту_навіть_без_прогріву_AN190()
    {
        using var lifetime = new TestLifetime();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = new List<string>();
        var service = Service(
            lifetime,
            new EditPathWarmupOptions(Enabled: false, MaxVersions: 5, WarmQuartz: false, WarmFirstEdit: true),
            [("blocked", async (_, _, ct) => { await gate.Task.WaitAsync(ct); ran.Add("blocked"); })]);

        await service.StartAsync(CancellationToken.None).WaitAsync(Safety);
        Assert.Null(service.Completion);

        // ⛔ Готовність: ApplicationStarted повертається, поки крок прогріву ще стоїть.
        lifetime.NotifyStarted();
        Assert.NotNull(service.Completion);
        Assert.False(service.Completion!.IsCompleted);

        gate.SetResult();
        await service.Completion.WaitAsync(Safety);
        Assert.Equal(["blocked"], ran);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Вимкнений_ключ_не_запускає_прогріву_першої_правки()
    {
        var options = EditPathWarmupOptions.Read(
            Config(new() { [FirstEditWarmer.EnabledKey] = "false", [EditPathWarmer.EnabledKey] = "false" }),
            warmQuartz: false,
            servesEdits: true);
        Assert.False(options.WarmFirstEdit);

        using var lifetime = new TestLifetime();
        var ran = false;
        var service = Service(lifetime, options, [("any", (_, _, _) => { ran = true; return Task.CompletedTask; })]);

        await service.StartAsync(CancellationToken.None);
        lifetime.NotifyStarted();

        Assert.Null(service.Completion);
        Assert.False(ran);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public void Ключ_і_процес_вирішують_разом_типово_увімкнено_лише_в_Api()
    {
        var empty = Config([]);
        Assert.True(FirstEditWarmer.ReadEnabled(empty));
        Assert.True(EditPathWarmupOptions.Read(empty, warmQuartz: false, servesEdits: true).WarmFirstEdit);

        // Дочірній воркер (servesEdits: false) зрізів і правок не обслуговує — і за ввімкненого ключа не гріє.
        Assert.False(EditPathWarmupOptions.Read(empty, warmQuartz: false).WarmFirstEdit);
        Assert.False(EditPathWarmupOptions.Read(
            Config(new() { [FirstEditWarmer.EnabledKey] = "true" }), warmQuartz: false, servesEdits: false).WarmFirstEdit);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Типові_кроки_кличуть_порти_лише_неіснуючими_ідентифікаторами_і_нічого_не_пишуть()
    {
        var rows = Substitute.For<IRowStore>();
        var cells = Substitute.For<ICellStore>();
        var periods = Substitute.For<IPeriodStore>();
        var headers = Substitute.For<IDocumentHeaderStore>();
        var units = Substitute.For<IUnitCatalog>();
        var methodologies = Substitute.For<IMethodologyStore>();
        var styles = Substitute.For<IStyleCatalog>();
        var formats = Substitute.For<IConditionalFormatStore>();
        var access = Substitute.For<IAccessDecisionService>();
        var cellStyles = Substitute.For<ICellStyleStore>();
        var overrides = Substitute.For<IFormulaOverrideStore>();
        var errors = Substitute.For<IFormulaErrorStore>();
        var permits = Substitute.For<IPermitRegistryIndex>();
        var windows = Substitute.For<IRowWindowColumnIndex>();
        object[] ports = [rows, cells, periods, headers, units, methodologies, styles, formats, access, cellStyles, overrides, errors, permits, windows];

        var services = new ServiceCollection();
        services.AddScoped(_ => rows).AddScoped(_ => cells).AddScoped(_ => periods).AddScoped(_ => headers)
            .AddScoped(_ => units).AddScoped(_ => methodologies).AddScoped(_ => styles).AddScoped(_ => formats)
            .AddScoped(_ => access).AddScoped(_ => cellStyles).AddScoped(_ => overrides).AddScoped(_ => errors)
            .AddScoped(_ => permits).AddScoped(_ => windows);
        provider = services.BuildServiceProvider();

        // Транзакція (база) і JIT (рефлексія) — поза цим тестом: перший потребує SQL Server, другий портів не кличе.
        var warmer = new FirstEditWarmer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FirstEditWarmer>.Instance)
        {
            Steps = [.. FirstEditWarmer.DefaultSteps.Where(s => s.Name is not ("transaction" or "jit"))],
        };

        var result = await warmer.WarmAsync([41, 42], CancellationToken.None).WaitAsync(Safety);

        Assert.Empty(result.Failed);

        // ⛔ Кожен ідентифікатор у виклику порту — «неіснуючий»; єдиний виняток — версії шаблону, що їх дав AN-190.
        var calls = ports.SelectMany(p => p.ReceivedCalls()).ToList();
        Assert.NotEmpty(calls);
        foreach (var call in calls)
        {
            foreach (var arg in call.GetArguments())
            {
                AssertNoRealId(call, arg);
            }
        }

        // ⛔ Лише читання: жодного запису комірок; зі служби доступу — тільки «проєкт документа» і профіль облікового
        // запису, якого немає (обидва — з NoId, перевірено вище); жодного рішення про документ чи комірку.
        Assert.All(cells.ReceivedCalls(), c => Assert.StartsWith("Read", c.GetMethodInfo().Name, StringComparison.Ordinal));
        Assert.All(access.ReceivedCalls(), c => Assert.Contains(
            c.GetMethodInfo().Name,
            new[] { nameof(IAccessDecisionService.DocumentProjectIdAsync), nameof(IAccessDecisionService.BuildProfileAsync) }));
        Assert.All(overrides.ReceivedCalls(), c => Assert.StartsWith("Read", c.GetMethodInfo().Name, StringComparison.Ordinal));
        Assert.All(errors.ReceivedCalls(), c => Assert.StartsWith("Read", c.GetMethodInfo().Name, StringComparison.Ordinal));
        Assert.Equal([41, 42], styles.ReceivedCalls().Select(c => (int)c.GetArguments()[0]!));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Збій_кроку_не_зупиняє_проходу()
    {
        provider = new ServiceCollection().BuildServiceProvider();
        var ran = new List<string>();
        var warmer = new FirstEditWarmer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FirstEditWarmer>.Instance)
        {
            Steps =
            [
                ("broken", (_, _, _) => throw new InvalidOperationException("крок")),
                ("next", (_, _, _) => { ran.Add("next"); return Task.CompletedTask; }),
            ],
        };

        var result = await warmer.WarmAsync([], CancellationToken.None).WaitAsync(Safety);

        Assert.Equal(["broken"], result.Failed);
        Assert.Equal(["next"], result.Done);
        Assert.Equal(["next"], ran);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task Скасування_зупиняє_прохід()
    {
        provider = new ServiceCollection().BuildServiceProvider();
        using var cts = new CancellationTokenSource();
        var ran = new List<string>();
        var warmer = new FirstEditWarmer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FirstEditWarmer>.Instance)
        {
            Steps =
            [
                ("first", async (_, _, ct) => { await cts.CancelAsync(); ct.ThrowIfCancellationRequested(); }),
                ("second", (_, _, _) => { ran.Add("second"); return Task.CompletedTask; }),
            ],
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warmer.WarmAsync([], cts.Token)).WaitAsync(Safety);
        Assert.Empty(ran);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    public async Task JIT_готує_код_обробників_зрізу_й_запису()
    {
        var targets = FirstEditWarmer.JitTargets();
        Assert.Contains(typeof(GetTableSliceHandler), targets);
        Assert.Contains(typeof(PatchCellsHandler), targets);
        Assert.Contains(typeof(Ecr.Infrastructure.Persistence.NormalizedCellStore), targets);

        Assert.True(await FirstEditWarmer.PrepareJit(CancellationToken.None).WaitAsync(Safety) > 0);
    }

    private static void AssertNoRealId(ICall call, object? arg)
    {
        var where = $"{call.GetMethodInfo().DeclaringType?.Name}.{call.GetMethodInfo().Name}";
        switch (arg)
        {
            case int id when call.GetMethodInfo().DeclaringType == typeof(IStyleCatalog)
                             || call.GetMethodInfo().DeclaringType == typeof(IConditionalFormatStore):
                Assert.True(id is 41 or 42, $"{where}: версія {id}");
                break;
            case int id when id != FirstEditWarmer.NoId && id != FirstEditWarmer.NoPeriod.Value:
                Assert.Fail($"{where}: справжній ідентифікатор {id}");
                break;
            case long id when id != FirstEditWarmer.NoId:
                Assert.Fail($"{where}: справжній ідентифікатор {id}");
                break;
            case PeriodKey key:
                Assert.Equal(FirstEditWarmer.NoPeriod, key);
                break;
            case CellAddress address:
                Assert.Equal(new CellAddress(FirstEditWarmer.NoPeriod, FirstEditWarmer.NoId, FirstEditWarmer.NoId), address);
                break;
            case System.Collections.IEnumerable items and not string:
                foreach (var item in items)
                {
                    AssertNoRealId(call, item);
                }

                break;
        }
    }

    private static IConfiguration Config(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private EditPathWarmupService Service(
        TestLifetime lifetime,
        EditPathWarmupOptions options,
        IReadOnlyList<(string Name, Func<IServiceProvider, IReadOnlyList<int>, CancellationToken, Task> Run)> steps)
    {
        provider = new ServiceCollection().BuildServiceProvider();
        return new EditPathWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(), lifetime, options,
            NullLogger<EditPathWarmupService>.Instance, NullLogger<EditPathWarmer>.Instance)
        {
            CreateFirstEditWarmer = scopes => new FirstEditWarmer(scopes, NullLogger<FirstEditWarmer>.Instance) { Steps = steps },
        };
    }

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
