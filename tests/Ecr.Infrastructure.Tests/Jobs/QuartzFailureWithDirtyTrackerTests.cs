// tests/Ecr.Infrastructure.Tests/Jobs/QuartzFailureWithDirtyTrackerTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>Задача, що лишає в спільному контексті не збережену сутність-дублікат і падає.</summary>
internal sealed class DirtyTrackerFailingJob(EcrDbContext db) : IBackgroundJob
{
    public static string DuplicateCode { get; set; } = "";

    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        db.Roles.Add(new Role(
            EcrCode.Create(DuplicateCode),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "dup" })));
        throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// RC15 (RACE-FIRST-COLLECT): збій задачі з «брудним» трекером не лишає її <c>Running</c>.
/// </summary>
/// <remarks>
/// Сховище прогресу ділить із задачею один <c>EcrDbContext</c>; не збережений дублікат у трекері валив
/// запис <c>Failed</c> (UQ), а збій запису прогресу ковтається - задача лишалась <c>Running</c> назавжди.
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>ChangeTracker.Clear()</c> у <c>catch</c> <c>QuartzJobAdapter</c> → стан <c>Running</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class QuartzFailureWithDirtyTrackerTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "RC15-race-first-collect")]
    public async Task Збій_задачі_з_незбереженим_дублікатом_у_трекері_завершується_Failed_а_не_Running()
    {
        var jobId = $"dirty-{Guid.NewGuid():N}";
        var code = $"DUP{Guid.NewGuid():N}"[..16];
        DirtyTrackerFailingJob.DuplicateCode = code;

        await using (var seed = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            seed.Roles.Add(new Role(
                EcrCode.Create(code),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "orig" })));
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<IClock>(new TestClock(new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc)));
        services.AddScoped<DirtyTrackerFailingJob>();
        await using var provider = services.BuildServiceProvider();

        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);
        await Assert.ThrowsAsync<JobExecutionException>(
            () => adapter.Execute(Context(jobId, QuartzJobAdapter.MaxRetryAttempts)));

        await using var check = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var state = await check.JobProgresses.AsNoTracking().Where(p => p.JobId == jobId).Select(p => p.State).SingleAsync();
        Assert.Equal("Failed", state.ToString());
    }

    private static IJobExecutionContext Context(string jobId, int attempt)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(DirtyTrackerFailingJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(jobId));
        jobDetail.JobDataMap.Returns(jobData);

        var triggerData = new JobDataMap();
        triggerData.Put(QuartzJobScheduler.RetryAttemptKey, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(triggerData);

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        return context;
    }
}
