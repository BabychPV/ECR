// tests/Ecr.Api.Tests/Health/ReportViewsHealthTests.cs
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Reporting;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// Картка <c>reportviews</c>: збій генерації вʼюх (50422/50409) — жовтий із назвою
/// шаблону й причиною, не 503; після успішного повтору — зелений.
/// </summary>
/// <remarks>
/// Мутація (прогнано): у <see cref="ReportViewsHealthCheck"/> замінити
/// <c>Degraded</c> на <c>Healthy</c> для непорожнього переліку → червоний.
/// </remarks>
public sealed class ReportViewsHealthTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("reportviews", Substitute.For<IHealthCheck>(), null, null),
    };

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_збоїв_зелений()
    {
        var result = await CheckAsync(new ReportViewStatus());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(50422, "таблиця 7 має 251 колонок")]
    [InlineData(50409, "дві таблиці дають одне ім'я вʼюхи rpt.v_A_B")]
    public async Task Збій_генерації_жовтий_з_шаблоном_версією_і_кодом_а_повтор_очищає(int code, string reason)
    {
        var status = new ReportViewStatus();
        status.Failed(new ReportViewFailure(12, code, $"шаблон WATER, версія 3: {reason}"));

        var result = await CheckAsync(status);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("WATER", result.Description, StringComparison.Ordinal);
        Assert.Contains(reason, result.Description, StringComparison.Ordinal);
        Assert.Contains($"[{code}]", result.Description, StringComparison.Ordinal);
        Assert.Equal("12", result.Data["templateVersionIds"]);

        status.Succeeded(12);

        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(status)).Status);
    }

    private static Task<HealthCheckResult> CheckAsync(IReportViewStatus status)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");
        return new ReportViewsHealthCheck(status, new FakeUiStringCatalog(), user)
            .CheckHealthAsync(Context, CancellationToken.None);
    }
}
