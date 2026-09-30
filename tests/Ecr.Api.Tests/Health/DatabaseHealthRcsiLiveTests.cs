// tests/Ecr.Api.Tests/Health/DatabaseHealthRcsiLiveTests.cs

using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// Перевірка <c>db</c> бачить RCSI таким, яким він є ЗАРАЗ, а не на старті
/// процесу (<c>U18</c>).
/// </summary>
/// <remarks>
/// ⚠ Застарілу пробу старту моделює підміна <see cref="ISqlCapabilities"/>, що
/// каже «RCSI вимкнено», поверх справжньої бази фікстури, де <c>06-rcsi.sql</c>
/// його ввімкнув. Це рівно сценарій runbook §5: DBA виконав <c>06-rcsi.sql</c>,
/// служба не перезапускалась. Перемикати RCSI на спільній тестовій базі не
/// можна — для цього потрібен монопольний доступ, тобто зупинка сусідніх тестів.
/// </remarks>
[Collection("SqlServer")]
public sealed class DatabaseHealthRcsiLiveTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "U18")]
    public async Task Увімкнений_після_старту_RCSI_видно_без_перезапуску()
    {
        var staleProbe = Substitute.For<ISqlCapabilities>();
        staleProbe.IsReadCommittedSnapshotOn.Returns(false);

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 9, 28, 0, 0, 0, DateTimeKind.Utc));

        await using var db = sql.CreateContext();
        var check = new DatabaseHealthCheck(
            staleProbe,
            db,
            clock,
            Substitute.For<IUiStringCatalog>(),
            Substitute.For<ICurrentUser>(),
            DataProtectionKeyProtection.Unprotected);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext
            {
                Registration = new HealthCheckRegistration("db", Substitute.For<IHealthCheck>(), null, null),
            },
            CancellationToken.None);

        // ⛔ До U18 тут стояло `false` з проби і статус «RCSI is disabled.».
        Assert.Equal(true, result.Data["rcsi"]);
        Assert.NotEqual("RCSI is disabled.", result.Description);

        var limitations = Assert.IsAssignableFrom<IReadOnlyList<string>>(result.Data["limitations"]);
        Assert.DoesNotContain(limitations, l => l.StartsWith("RCSI is disabled", StringComparison.Ordinal));
    }
}
