// tests/Ecr.Api.Tests/Health/DatabaseHealthEdgeWarningTests.cs

using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// PSS-L6b: <c>edgeWarning</c> нічної <c>PartitionCheck</c> видно в даних перевірки <c>db</c> (картка «Здоров'я системи»),
/// але статус перевірки від нього не міняється (runbook §7.5).
/// </summary>
[Collection("SqlServer")]
public sealed class DatabaseHealthEdgeWarningTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Останній_прогін_з_edgeWarning_видно_в_даних_а_статус_не_гірший()
    {
        var baseline = await CheckAsync(null);
        var result = await CheckAsync("{\"boundariesAhead\":15,\"edgeRows\":{\"first\":7,\"last\":0},\"edgeWarning\":true}");

        Assert.Equal(true, result.Data["edgeWarning"]);
        Assert.Equal(7L, result.Data["edgeRowsFirst"]);
        Assert.Equal(0L, result.Data["edgeRowsLast"]);

        // Попередження не опускає статус: той самий, що й без нього на тій самій базі.
        Assert.Equal(baseline.Status, result.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(baseline.Description, result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_без_edgeWarning_або_без_прогону_дає_false()
    {
        var none = await CheckAsync(null);
        Assert.Equal(false, none.Data["edgeWarning"]);

        var clean = await CheckAsync("{\"boundariesAhead\":15,\"edgeRows\":{\"first\":0,\"last\":0}}");
        Assert.Equal(false, clean.Data["edgeWarning"]);
        Assert.False(clean.Data.ContainsKey("edgeRowsFirst"));
    }

    /// <summary>Кладе прогін <c>partition-check</c> із цими подробицями (якщо задано) і виконує перевірку; прогін прибирає.</summary>
    private async Task<HealthCheckResult> CheckAsync(string? detailsJson)
    {
        await using var seed = sql.CreateContext();
        await seed.MaintenanceRuns.Where(r => r.JobCode == PartitionCheckJob.Code).ExecuteDeleteAsync();
        long? runId = null;
        if (detailsJson is not null)
        {
            var run = new MaintenanceRun(PartitionCheckJob.Code, Now);
            run.Complete("Succeeded", detailsJson, Now.AddSeconds(1));
            seed.MaintenanceRuns.Add(run);
            await seed.SaveChangesAsync();
            runId = run.Id;
        }

        try
        {
            var capabilities = Substitute.For<ISqlCapabilities>();
            capabilities.SupportsOnlineIndexRebuild.Returns(true);
            capabilities.SupportsResourceGovernor.Returns(true);

            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);

            var host = Substitute.For<IHostEnvironment>();
            host.EnvironmentName.Returns(Environments.Development);
            host.ContentRootFileProvider.Returns(new NullFileProvider());

            await using var db = sql.CreateContext();
            var check = new DatabaseHealthCheck(
                capabilities,
                db,
                clock,
                Substitute.For<IUiStringCatalog>(),
                Substitute.For<ICurrentUser>(),
                DataProtectionKeyProtection.Unprotected,
                host);

            return await check.CheckHealthAsync(
                new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
                },
                CancellationToken.None);
        }
        finally
        {
            if (runId is { } id)
            {
                await using var cleanup = sql.CreateContext();
                await cleanup.MaintenanceRuns.Where(r => r.Id == id).ExecuteDeleteAsync();
            }
        }
    }
}
