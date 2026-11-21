// tests/Ecr.Api.Tests/NightlyRecalculationSchedulingTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Нічний перерахунок: ✎ PSS-15 (<c>D-562</c>, рішення людини 21.11) — УВІМКНЕНИЙ за замовчуванням, вимикає лише явне
/// <c>false</c>; тригер — лише на АКТИВНІ проєкти, з позначкою нічного розкладу й режимом пропуску незмінних; час — до вікна
/// прибирання замінених прогонів.
/// </summary>
/// <remarks>
/// Доти (Q-331) — опція, вимкнена за замовчуванням. МУТАЦІЯ: <c>Flag</c> повертає <c>false</c> на порожньому значенні —
/// червоний <see cref="Порожня_конфігурація_ставить_нічний_на_активні_проєкти"/>; <c>Cron</c> назад на 03:30 — червоний
/// <see cref="Нічний_старт_до_вікна_прибирання_і_до_нічних_перевірок"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class NightlyRecalculationSchedulingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-15")]
    public async Task Порожня_конфігурація_ставить_нічний_на_активні_проєкти()
    {
        var (db, activeProjectId, draftProjectId) = await ArrangeAsync();
        await using var scope = db;

        // ⚠ Порожня конфігурація — той самий стан, що й на проді без жодного рядка `Jobs:NightlyRecalculation:*`.
        var configuration = new ConfigurationBuilder().Build();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        var scheduled = await NightlyRecalculationScheduling
            .ScheduleAsync(configuration, db, scheduler, CancellationToken.None);

        Assert.True(scheduled >= 1);
        await scheduler.Received()
            .ScheduleAsync<IRecalculationJob>(
                NightlyRecalculationScheduling.Cron,
                Arg.Is<object?>(p => Is(p, "ProjectId", activeProjectId) && Is(p, "Nightly", true)
                                     && Is(p, "NightlySkipUnchanged", true)),
                Arg.Any<CancellationToken>());

        // Проєкт у Draft — жодного тригера.
        await scheduler.DidNotReceive()
            .ScheduleAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Is<object?>(p => Is(p, "ProjectId", draftProjectId)), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-331")]
    public async Task Прапорець_явно_вимкнений_не_ставить_нічого()
    {
        var (db, _, _) = await ArrangeAsync();
        await using var scope = db;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(NightlyRecalculationScheduling.EnabledKey, "false")])
            .Build();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        var scheduled = await NightlyRecalculationScheduling
            .ScheduleAsync(configuration, db, scheduler, CancellationToken.None);

        Assert.Equal(0, scheduled);
        await scheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync<IRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "PSS-15")]
    public async Task Вимкнений_пропуск_незмінних_іде_в_payload_тригера()
    {
        var (db, activeProjectId, _) = await ArrangeAsync();
        await using var scope = db;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(NightlyRecalculationScheduling.SkipUnchangedKey, "false")])
            .Build();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        await NightlyRecalculationScheduling.ScheduleAsync(configuration, db, scheduler, CancellationToken.None);

        await scheduler.Received()
            .ScheduleAsync<IRecalculationJob>(
                NightlyRecalculationScheduling.Cron,
                Arg.Is<object?>(p => Is(p, "ProjectId", activeProjectId) && Is(p, "NightlySkipUnchanged", false)),
                Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// ✎ PSS-15: 00:30 — ДО типового вікна прибирання замінених прогонів (01:00–05:00, задача о 02:15) і до нічних перевірок
    /// (02:15): перерахунок і пакетне видалення з <c>calc.*</c> не йдуть одночасно.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "PSS-15")]
    public void Нічний_старт_до_вікна_прибирання_і_до_нічних_перевірок()
    {
        var day = new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);
        var recalc = new CronExpression(NightlyRecalculationScheduling.Cron) { TimeZone = TimeZoneInfo.Utc }
            .GetTimeAfter(day)!.Value.TimeOfDay;
        var checks = new CronExpression(RecurringScheduleService.NightlyCron) { TimeZone = TimeZoneInfo.Utc }
            .GetTimeAfter(day)!.Value.TimeOfDay;

        Assert.Equal(TimeSpan.FromMinutes(30), recalc);
        Assert.True(recalc < Infrastructure.Persistence.SupersededPurgeOptions.DefaultNightWindowStart);
        Assert.True(recalc < checks);
    }

    private async Task<(Infrastructure.Persistence.EcrDbContext Db, int Active, int Draft)> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var activeDoc = await builder.BuildAsync();
        var draftDoc = await builder.BuildAsync();

        await using (var seed = builder.CreateContext())
        {
            var project = await seed.Projects.FindAsync(activeDoc.ProjectId);
            project!.Activate(DateTime.UtcNow);
            await seed.SaveChangesAsync();
        }

        return (builder.CreateContext(), activeDoc.ProjectId, draftDoc.ProjectId);
    }

    /// <summary>Чи має анонімний payload властивість зі значенням — рефлексією.</summary>
    private static bool Is<T>(object? payload, string property, T expected)
        => payload?.GetType().GetProperty(property)?.GetValue(payload) is T value
           && EqualityComparer<T>.Default.Equals(value, expected);
}
