using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// Картка <c>db</c>: розмір <c>ext.RawDataPoint</c> проти порога перегляду R2 (20 млн рядків).
/// Мутації: <c>&gt;=</c> → <c>&gt;</c> у <c>Evaluate</c> (падає 20 млн); підміна ключа (падає перевірка messageKey);
/// 80 % → 100 % (падає 16 млн).
/// </summary>
[Collection("SqlServer")]
public sealed class RawDataPointHealthTests(SqlServerFixture sql)
{
    private const long Threshold = 20_000_000;

    [Theory]
    [InlineData(0L, HealthStatus.Healthy, null)]
    [InlineData(15_999_999L, HealthStatus.Healthy, null)]
    [InlineData(16_000_000L, HealthStatus.Degraded, "health.collection.rawPointsApproaching")]
    [InlineData(19_999_999L, HealthStatus.Degraded, "health.collection.rawPointsApproaching")]
    [InlineData(20_000_000L, HealthStatus.Degraded, "health.collection.rawPointsOverThreshold")]
    [InlineData(50_000_000L, HealthStatus.Degraded, "health.collection.rawPointsOverThreshold")]
    public void Evaluate_за_рядками_і_порогом(long rows, HealthStatus status, string? key)
    {
        var (actualStatus, actualKey) = RawDataPointHealth.Evaluate(rows, Threshold);

        Assert.Equal(status, actualStatus);
        Assert.Equal(key, actualKey);
    }

    [Fact]
    public void Evaluate_нульовий_поріг_вимикає_перевірку()
    {
        Assert.Equal((HealthStatus.Healthy, (string?)null), RawDataPointHealth.Evaluate(100_000_000, 0));
    }

    [Fact]
    public void Параметри_тексту_rows_і_threshold()
    {
        var p = RawDataPointHealth.Parameters(16_000_000, Threshold);

        Assert.Equal("16,000,000", p["rows"]);
        Assert.Equal("20,000,000", p["threshold"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кількість_рядків_з_sys_partitions_невід_ємна_і_не_кидає()
    {
        await using var db = sql.CreateContext();

        var rows = await RawDataPointHealth.ApproximateRowsAsync(
            db, new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        Assert.True(rows >= 0);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Картка_db_віддає_кількість_і_поріг_у_даних_і_статус_за_Evaluate()
    {
        var capabilities = Substitute.For<ISqlCapabilities>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Health:RawDataPointWarnRows", "1")])
            .Build();

        await using var db = sql.CreateContext();
        var check = new DatabaseHealthCheck(
            capabilities,
            db,
            clock,
            Substitute.For<IUiStringCatalog>(),
            Substitute.For<ICurrentUser>(),
            DataProtectionKeyProtection.Unprotected,
            configuration: configuration);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
            },
            CancellationToken.None);

        var rows = Assert.IsType<long>(result.Data["rawDataPointRows"]);
        Assert.Equal(1L, result.Data["rawDataPointWarnRows"]);
        var (expected, key) = RawDataPointHealth.Evaluate(rows, 1);
        Assert.Equal(expected, result.Status);
        if (key is not null)
        {
            Assert.Contains("ext.RawDataPoint", result.Description, StringComparison.Ordinal);
        }
    }
}
