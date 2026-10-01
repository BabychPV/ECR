// tests/Ecr.Api.Tests/Health/TimeZoneDatabaseHostTests.cs
using System.Text.Json;
using Ecr.Api.Health;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// F-4: застаріла база часових поясів НЕ блокує запуск сервера.
/// </summary>
/// <remarks>
/// ⛔ Справжній хост із підміненою «застарілою» базою піднімається,
/// <c>/health/ready</c> відповідає 200 (не 503), картка <c>tzdata</c> — Degraded, а
/// причина лягла в журнал старту попередженням. Мутація (прогнано): кинути виняток
/// у <c>ReportTimeZoneDatabase</c> → хост не піднімається, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class TimeZoneDatabaseHostTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F-4")]
    public async Task Хост_із_застарілою_базою_поясів_стартує_а_готовність_жовта_з_попередженням_у_журналі()
    {
        using var host = new EcrApiFactory(sql);
        using var app = host.WithWebHostBuilder(b => b.ConfigureTestServices(
            s => s.AddSingleton<ITimeZoneOffsetProvider>(FixedTimeZoneOffsetProvider.StaleAlmaty)));
        using var client = app.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.NotEqual(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");

        var report = JsonDocument.Parse(body).RootElement;
        var tzdata = report.GetProperty("checks")
            .EnumerateArray()
            .Single(c => string.Equals(c.GetProperty("name").GetString(), "tzdata", StringComparison.Ordinal));

        Assert.Equal("Degraded", tzdata.GetProperty("status").GetString());
        Assert.Contains(
            "Asia/Almaty=+06:00", tzdata.GetProperty("description").GetString(), StringComparison.Ordinal);

        Assert.Contains(
            host.ServerLog,
            line => line.Contains("[Ecr.Startup]", StringComparison.Ordinal)
                    && line.Contains("Asia/Almaty=+06:00", StringComparison.Ordinal));
    }
}
