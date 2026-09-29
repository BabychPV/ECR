// tests/Ecr.Api.Tests/Health/DatabaseHealthUnprotectedKeysTests.cs

using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// S11: у Production незахищене кільце (можливе лише за явної згоди стенда
/// <c>Auth:DataProtection:AllowUnprotectedKeys</c>) робить перевірку <c>db</c>
/// Degraded із названою причиною.
/// </summary>
[Collection("SqlServer")]
public sealed class DatabaseHealthUnprotectedKeysTests(SqlServerFixture sql)
{
    private const string UnprotectedPrefix = "Session keys are stored unencrypted";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Production_без_захисту_за_згодою_стенда_Degraded_з_причиною()
    {
        var result = await CheckAsync(DataProtectionKeyProtection.Unprotected, Environments.Production);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.StartsWith(UnprotectedPrefix, result.Description, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Development_без_захисту_ключів_лише_обмеження_як_було()
    {
        var result = await CheckAsync(DataProtectionKeyProtection.Unprotected, Environments.Development);

        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
        Assert.False(
            result.Description?.StartsWith(UnprotectedPrefix, StringComparison.Ordinal) ?? false,
            "Поза Production незахищене кільце — лише рядок обмежень, не стан перевірки.");

        var limitations = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Data["limitations"]);
        Assert.Contains(limitations, l => l.StartsWith(UnprotectedPrefix, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Production_із_сертифікатом_без_стану_через_ключі()
    {
        var result = await CheckAsync(
            DataProtectionKeyProtection.ProtectedBy(new string('A', 40)), Environments.Production);

        Assert.NotEqual(HealthStatus.Unhealthy, result.Status);
        Assert.False(
            result.Description?.StartsWith(UnprotectedPrefix, StringComparison.Ordinal) ?? false,
            "Із сертифікатом стан перевірки не може посилатися на відкриті ключі.");

        var limitations = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Data["limitations"]);
        Assert.DoesNotContain(limitations, l => l.StartsWith(UnprotectedPrefix, StringComparison.Ordinal));
    }

    private async Task<HealthCheckResult> CheckAsync(DataProtectionKeyProtection protection, string environment)
    {
        var capabilities = Substitute.For<ISqlCapabilities>();
        capabilities.SupportsOnlineIndexRebuild.Returns(true);
        capabilities.SupportsResourceGovernor.Returns(true);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc));

        var host = Substitute.For<IHostEnvironment>();
        host.EnvironmentName.Returns(environment);
        host.ContentRootFileProvider.Returns(new NullFileProvider());

        await using var db = sql.CreateContext();
        var check = new DatabaseHealthCheck(
            capabilities,
            db,
            clock,
            Substitute.For<IUiStringCatalog>(),
            Substitute.For<ICurrentUser>(),
            protection,
            host);

        return await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
            },
            CancellationToken.None);
    }
}
