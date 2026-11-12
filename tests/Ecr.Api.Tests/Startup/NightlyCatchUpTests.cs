// tests/Ecr.Api.Tests/Startup/NightlyCatchUpTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Наздоганяння нічних задач на старті (<see cref="NightlyCatchUp"/>).
/// </summary>
/// <remarks>
/// ⛔ Аудит live 12.11: у <c>itg.MaintenanceRun</c> немає жодної нічної задачі за 15, 17, 20, 23, 25, 31.10, 01.11
/// і 12.11 — застосунок не працював о 02:15, а cron пропущеного тику не повторює. Мутаційні докази: прибрати
/// постановку в <c>EnqueueMissedAsync</c> (або порівняння з <see cref="NightlyCatchUp.MaxAge"/>) — перший тест
/// червоніє; ставити й задачу без жодної відмітки — третій.
/// </remarks>
public sealed class NightlyCatchUpTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 13, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Задача_остання_відмітка_якої_старша_за_добу_ставиться_в_чергу_одразу()
    {
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var lastRuns = new Dictionary<string, DateTime>(StringComparer.Ordinal)
        {
            // 12.11 00:15 пропущено: остання перевірка — 11.11 00:15, тобто 36 год 45 хв тому.
            [ConsistencyCheckJob.Code] = new(2026, 11, 11, 0, 15, 0, DateTimeKind.Utc),
            // Свіжа — не чіпаємо.
            [SupersededResultsSweepJob.Code] = Now.AddHours(-2),
        };

        var missed = await NightlyCatchUp.EnqueueMissedAsync(lastRuns, scheduler, Now, CancellationToken.None);

        Assert.Equal([ConsistencyCheckJob.Code], missed);
        await scheduler.Received(1).EnqueueCoalescedAsync<ConsistencyCheckJob>(
            "nightly-catch-up:" + ConsistencyCheckJob.Code, null, Arg.Any<CancellationToken>());
        await scheduler.DidNotReceive().EnqueueCoalescedAsync<SupersededResultsSweepJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    [Fact]
    public async Task Прибирання_ставиться_раніше_за_перевірку_узгодженості()
    {
        // ⚠ Порядок постановки: прибирання першим, перевірка — останньою (межа NON_CURRENT_RUN_ROWS спирається на
        // старт останнього успішного прибирання).
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var stale = Now.AddDays(-3);
        var lastRuns = NightlyCatchUp.Codes.ToDictionary(code => code, _ => stale, StringComparer.Ordinal);

        var missed = await NightlyCatchUp.EnqueueMissedAsync(lastRuns, scheduler, Now, CancellationToken.None);

        Assert.Equal(NightlyCatchUp.Codes, missed);
        Assert.Equal(SupersededResultsSweepJob.Code, missed[0]);
        Assert.Equal(ConsistencyCheckJob.Code, missed[^1]);
    }

    [Fact]
    public async Task Задача_без_жодної_відмітки_не_наздоганяється()
    {
        // Свіжа база (розгортання, стенд smoke/e2e): наздоганяти нічого — перший прохід зробить нічний cron.
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        var missed = await NightlyCatchUp.EnqueueMissedAsync(
            new Dictionary<string, DateTime>(StringComparer.Ordinal), scheduler, Now, CancellationToken.None);

        Assert.Empty(missed);
        Assert.Empty(scheduler.ReceivedCalls());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Увімкнено_за_замовчуванням_і_вимикається_ключем(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NightlyCatchUp.EnabledKey] = value })
            .Build();

        Assert.Equal(expected, NightlyCatchUp.IsEnabled(configuration));
    }
}
