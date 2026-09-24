// tests/Ecr.Infrastructure.Tests/Jobs/QuartzJobDatabaseFailureTests.cs
using Ecr.Application.Ports;
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

/// <summary>Задача, що падає СПРАВЖНІМ порушенням первинного ключа SQL Server.</summary>
internal sealed class PrimaryKeyViolatingJob(EcrDbContext db) : IBackgroundJob
{
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync(
            "DECLARE @cells TABLE (k int PRIMARY KEY); INSERT INTO @cells VALUES (1), (1);", ct);
}

/// <summary>
/// Провал задачі на помилці бази: без сирого SQL у <c>/jobs</c> і без
/// марних повторів (V-03, UX-прохід 2026-09-24).
/// </summary>
/// <remarks>
/// ⛔ До виправлення задача перерахунку з <c>Violation of PRIMARY KEY … dbo.@cells</c>
/// (а) ретраїла 30 + 60 + 120 с, показуючи «виконується», хоча той самий вхід
/// дає той самий конфлікт, і (б) віддавала в <c>/jobs</c> сирий текст
/// SqlException — імена об'єктів бази й значення ключа. Виняток тут —
/// справжній, від справжнього SQL Server, а не підроблений: <c>SqlException</c>
/// не має публічного конструктора, і саме номер помилки сервера вирішує
/// «повторювати чи ні».
/// </remarks>
[Collection("SqlServer")]
public sealed class QuartzJobDatabaseFailureTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порушення_ключа_бази_валить_задачу_одразу_і_не_показує_сирий_SQL()
    {
        var jobId = $"pkfail-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(
            new TestClock(new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc)));
        services.AddScoped<PrimaryKeyViolatingJob>();
        await using var provider = services.BuildServiceProvider();

        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);

        // ⛔ Перша ж спроба — провал, а не ретрай: до виправлення тут
        // повертався звичайний `return` після планування повтору.
        await Assert.ThrowsAsync<JobExecutionException>(() => adapter.Execute(Context(jobId)));

        await using var db = sql.CreateContext();
        var status = await new JobProgressStore(db).FindAsync(jobId, CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal("Failed", status.State);
        Assert.Equal("ECR-SYS-0500", status.ErrorCode);

        // ⛔ Головне: жодного тексту сервера бази.
        Assert.DoesNotContain("PRIMARY KEY", status.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@cells", status.Error!, StringComparison.Ordinal);
        Assert.Contains("database error", status.Error!, StringComparison.Ordinal);
    }

    private static IJobExecutionContext Context(string jobId)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(PrimaryKeyViolatingJob).FullName! },
            { QuartzJobScheduler.PayloadKey, "null" },
        };

        var jobDetail = Substitute.For<IJobDetail>();
        jobDetail.Key.Returns(new JobKey(jobId));
        jobDetail.JobDataMap.Returns(jobData);

        var trigger = Substitute.For<ITrigger>();
        trigger.JobDataMap.Returns(new JobDataMap());

        var context = Substitute.For<IJobExecutionContext>();
        context.JobDetail.Returns(jobDetail);
        context.Trigger.Returns(trigger);
        context.Scheduler.Returns(Substitute.For<IScheduler>());
        context.CancellationToken.Returns(CancellationToken.None);

        return context;
    }
}
