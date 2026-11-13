// tests/Ecr.Api.Tests/Startup/NightlyCatchUpTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
            "nightly-catch-up", null, Arg.Any<CancellationToken>());
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
    public async Task Ідентифікатор_кожної_задачі_наздоганяння_вміщається_в_колонки_JobProgress()
    {
        // ⛔ Live 13.11 (приймання): «SubmissionSnapshotRetentionJob~nightly-catch-up:submission-snapshot-retention~<guid>»
        // = 110 символів при itg.JobProgress.JobId nvarchar(100) — SqlException, і решта переліку не ставилась.
        // Довжини — з моделі EF (не з числа в тесті): звузять колонку — тест почервоніє.
        using var db = ModelOnlyContext();
        var entity = db.Model.FindEntityType(typeof(JobProgress))!;
        var jobIdMax = entity.FindProperty(nameof(JobProgress.JobId))!.GetMaxLength()!.Value;
        var targetKeyMax = entity.FindProperty(nameof(JobProgress.TargetKey))!.GetMaxLength()!.Value;

        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        var lastRuns = NightlyCatchUp.Codes.ToDictionary(code => code, _ => Now.AddDays(-3), StringComparer.Ordinal);

        await NightlyCatchUp.EnqueueMissedAsync(lastRuns, scheduler, Now, CancellationToken.None);

        var calls = scheduler.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync))
            .Select(c => (Type: c.GetMethodInfo().GetGenericArguments()[0].Name, Key: (string)c.GetArguments()[0]!))
            .ToList();
        Assert.Equal(NightlyCatchUp.Codes.Count, calls.Count);

        foreach (var (type, key) in calls)
        {
            // Форма QuartzJobScheduler.TargetPrefixOf + Guid("N") (CoalesceAsync): {Тип}~{ціль}~{32 hex}.
            var quartzJobId = $"{type}~{key.Replace('~', '_')}~{Guid.NewGuid():N}";
            // Форма DbBackgroundJobScheduler.TargetKeyOf: {Тип}~{ціль}.
            var dbTargetKey = $"{type}~{key.Replace('~', '_')}";

            Assert.True(
                quartzJobId.Length <= jobIdMax,
                $"JobId {quartzJobId} — {quartzJobId.Length} символів, колонка JobProgress.JobId — {jobIdMax}.");
            Assert.True(
                dbTargetKey.Length <= targetKeyMax,
                $"TargetKey {dbTargetKey} — {dbTargetKey.Length} символів, колонка JobProgress.TargetKey — {targetKeyMax}.");
        }
    }

    [Fact]
    public async Task Збій_постановки_однієї_задачі_не_зупиняє_решту()
    {
        // ⛔ Live 13.11: виняток постановки SubmissionSnapshotRetentionJob обривав цикл, і перевірка узгодженості
        // (остання в переліку) не наздоганялась. Тепер решта ставиться, а збій повертається одним винятком.
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        scheduler.EnqueueCoalescedAsync<SubmissionSnapshotRetentionJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(Task.FromException<string>(new InvalidOperationException("String or binary data would be truncated")));
        var lastRuns = NightlyCatchUp.Codes.ToDictionary(code => code, _ => Now.AddDays(-3), StringComparer.Ordinal);

        var error = await Record.ExceptionAsync(
            () => NightlyCatchUp.EnqueueMissedAsync(lastRuns, scheduler, Now, CancellationToken.None));

        await scheduler.Received(1).EnqueueCoalescedAsync<ConsistencyCheckJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        await scheduler.Received(1).EnqueueCoalescedAsync<SupersededResultsSweepJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
        Assert.NotNull(error);
        Assert.Contains(SubmissionSnapshotRetentionJob.Code, error.Message, StringComparison.Ordinal);
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Скасування_не_ковтається()
    {
        var scheduler = Substitute.For<IBackgroundJobScheduler>();
        scheduler.EnqueueCoalescedAsync<SupersededResultsSweepJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(Task.FromException<string>(new OperationCanceledException()));
        var lastRuns = NightlyCatchUp.Codes.ToDictionary(code => code, _ => Now.AddDays(-3), StringComparer.Ordinal);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => NightlyCatchUp.EnqueueMissedAsync(lastRuns, scheduler, Now, CancellationToken.None));
        await scheduler.DidNotReceive().EnqueueCoalescedAsync<ConsistencyCheckJob>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>());
    }

    private static EcrDbContext ModelOnlyContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(model-only-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);

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
