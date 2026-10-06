// tests/Ecr.Api.Tests/JobFailureTextApiTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Quartz;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>Задача, що падає винятком із рядком підключення і шляхом файлу.</summary>
internal sealed class SecretLeakingJob : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        => throw new InvalidOperationException(
            @"Server=db01;Password=Secret123;Database=Ecr; cfg C:\Users\svc\app\secret.cfg");
}

/// <summary>
/// <c>GET /api/v1/jobs/{id}</c> і перелік не віддають внутрішніх подробиць
/// винятку задачі (SEC, TIER2).
/// </summary>
/// <remarks>
/// ⛔ Мутація: повернути <c>error.Message</c> у <c>JobFailureText.For</c> — тест
/// червоний: у відповіді з'являється «Secret123».
/// </remarks>
[Collection("SqlServer")]
public sealed class JobFailureTextApiTests(SqlServerFixture sql)
{
    private static readonly string[] Secrets = ["Password", "Secret123", "db01", "secret.cfg", @"C:\Users\svc"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Провалена_задача_віддає_код_і_кореляцію_а_не_текст_винятку()
    {
        var jobId = $"secfail-{Guid.NewGuid():N}";

        var services = new ServiceCollection();
        services.AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString));
        services.AddScoped<IJobProgressStore, JobProgressStore>();
        services.AddSingleton<IClock>(new TestClock(new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc)));
        services.AddScoped<SecretLeakingJob>();
        await using var provider = services.BuildServiceProvider();

        // Остання спроба: ліміт ретраїв вичерпано, задача стає Failed.
        var adapter = new QuartzJobAdapter(provider, NullLogger<QuartzJobAdapter>.Instance);
        await Assert.ThrowsAsync<JobExecutionException>(
            () => adapter.Execute(Context(jobId, QuartzJobAdapter.MaxRetryAttempts)));

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests
            .SignedInAsync(sql, app, "System.ViewHealth")
            .ConfigureAwait(true);

        var response = await client
            .GetAsync(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative))
            .ConfigureAwait(true);
        var raw = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {raw}");

        var status = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("Failed", status.GetProperty("state").GetString());
        Assert.Equal("ECR-SYS-0500", status.GetProperty("errorCode").GetString());

        var error = status.GetProperty("error").GetString()!;
        Assert.Contains("ECR-SYS-0500", error, StringComparison.Ordinal);
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IJobExecutionContext Context(string jobId, int attempt)
    {
        var jobData = new JobDataMap
        {
            { QuartzJobScheduler.JobCodeKey, typeof(SecretLeakingJob).FullName! },
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
