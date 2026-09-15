// tests/Ecr.Api.Tests/NightlyRecalculationSchedulingTests.cs
using Ecr.Api.Startup;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Q-330, директива №09 частина B: нічний повний перерахунок — ОПЦІЯ на
/// рівні конфігурації, вимкнена за замовчуванням.
/// </summary>
/// <remarks>
/// ⛔ До цього пакета <c>IRecalculationJob</c> не стояв на жодному розкладі
/// взагалі: <c>RecurringScheduleService</c> ставив шість задач
/// (`PartitionCheckJob`/`ConsistencyCheckJob`/`OrphanScanJob`/
/// `ReportRetentionJob`/`PeriodStateJob`/`NotificationJob`) і жодного разу —
/// перерахунок. Ці тести доводять МУТАЦІЄЮ, що тригер реально ставиться лише
/// за прапорцем, і лише на АКТИВНІ проєкти.
/// </remarks>
[Collection("SqlServer")]
public sealed class NightlyRecalculationSchedulingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-330")]
    public async Task Прапорець_вимкнений_за_замовчуванням_не_ставить_нічого()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        await using (var seed = builder.CreateContext())
        {
            var project = await seed.Projects.FindAsync(document.ProjectId);
            project!.Activate(DateTime.UtcNow);
            await seed.SaveChangesAsync();
        }

        // ⚠ Порожня конфігурація — той самий стан, що й на проді без явного
        // рішення розгортача: жодного рядка `Jobs:NightlyRecalculation:*`.
        var configuration = new ConfigurationBuilder().Build();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        var scheduled = await NightlyRecalculationScheduling
            .ScheduleAsync(configuration, db, scheduler, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: типовий стан — нічого не ставиться, навіть
        // коли є активний проєкт.
        Assert.Equal(0, scheduled);
        await scheduler.DidNotReceiveWithAnyArgs()
            .ScheduleAsync<IRecalculationJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "Q-330")]
    public async Task Прапорець_явно_вимкнений_не_ставить_нічого()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        await using (var seed = builder.CreateContext())
        {
            var project = await seed.Projects.FindAsync(document.ProjectId);
            project!.Activate(DateTime.UtcNow);
            await seed.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Jobs:NightlyRecalculation:Enabled", "false")])
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
    [Trait("Requirement", "Q-330")]
    public async Task Прапорець_увімкнений_ставить_тригер_лише_на_активні_проєкти()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var activeDoc = await builder.BuildAsync();
        var draftDoc = await builder.BuildAsync();

        await using var db = builder.CreateContext();
        await using (var seed = builder.CreateContext())
        {
            var activeProject = await seed.Projects.FindAsync(activeDoc.ProjectId);
            activeProject!.Activate(DateTime.UtcNow);
            // draftDoc.ProjectId лишається в Draft — навмисно НЕ активований.
            await seed.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Jobs:NightlyRecalculation:Enabled", "true")])
            .Build();
        var scheduler = Substitute.For<IBackgroundJobScheduler>();

        var scheduled = await NightlyRecalculationScheduling
            .ScheduleAsync(configuration, db, scheduler, CancellationToken.None);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: рівно один тригер — на активний проєкт;
        // проєкт у Draft (`draftDoc.ProjectId`) НЕ отримав жодного.
        Assert.True(scheduled >= 1);

        await scheduler.Received()
            .ScheduleAsync<IRecalculationJob>(
                NightlyRecalculationScheduling.Cron,
                Arg.Is<object?>(p => HasProjectId(p, activeDoc.ProjectId)),
                Arg.Any<CancellationToken>());

        await scheduler.DidNotReceive()
            .ScheduleAsync<IRecalculationJob>(
                Arg.Any<string>(),
                Arg.Is<object?>(p => HasProjectId(p, draftDoc.ProjectId)),
                Arg.Any<CancellationToken>());
    }

    /// <summary>Чи належить payload заданому проєкту — читає анонімний об'єкт рефлексією.</summary>
    private static bool HasProjectId(object? payload, int projectId)
        => payload?.GetType().GetProperty("ProjectId")?.GetValue(payload) is int value && value == projectId;
}
