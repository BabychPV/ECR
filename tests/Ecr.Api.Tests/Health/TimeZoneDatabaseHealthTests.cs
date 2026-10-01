// tests/Ecr.Api.Tests/Health/TimeZoneDatabaseHealthTests.cs
using Ecr.Api.Health;
using Ecr.Application.Common;
using Ecr.TestKit;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests.Health;

/// <summary>
/// F-4: перевірка <c>tzdata</c> жовтіє, коли база часових поясів ОС не знає, що
/// з 2024-03-01 Казахстан на UTC+5, — і ніколи не червоніє й не блокує старт.
/// </summary>
/// <remarks>
/// Зсуви підмінено (<see cref="FixedTimeZoneOffsetProvider"/>): справжню базу
/// машини тест не змінить. Справжню базу цієї машини перевіряє
/// <c>KazakhstanSiteZoneOffsetTests</c> у <c>Ecr.Domain.Tests</c>.
/// </remarks>
public sealed class TimeZoneDatabaseHealthTests
{
    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("tzdata", Substitute.For<IHealthCheck>(), null, null),
    };

    /// <remarks>
    /// ⛔ Головний випадок. Мутація (прогнано): у перевірці повернути <c>Healthy</c>
    /// безумовно (прибрати попередження) → застаріла база дає зелений, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "F-4")]
    public async Task Застаріла_база_Almaty_плюс_шість_жовтий_і_називає_пояс()
    {
        var result = await CheckAsync(FixedTimeZoneOffsetProvider.StaleAlmaty);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Asia/Almaty=+06:00", result.Data["staleZones"]);
        Assert.Contains("Asia/Almaty=+06:00", result.Description, StringComparison.Ordinal);
        Assert.Contains("tzdata", result.Description, StringComparison.Ordinal);

        // Пояси, чиї зсуви збіглися, у скаргу не потрапляють.
        Assert.DoesNotContain("Atyrau", result.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("Aqtau", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "F-4")]
    public async Task База_що_знає_перехід_зелена_без_застарілих_поясів()
    {
        var result = await CheckAsync(FixedTimeZoneOffsetProvider.Current);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.False(result.Data.ContainsKey("staleZones"));
        Assert.Equal("+05:00", result.Data["expectedOffset"]);
    }

    /// <remarks>
    /// Контейнер із урізаним tzdata: пояса немає взагалі — теж жовтий, а не
    /// «нічого не знайдено, отже все гаразд».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "F-4")]
    public async Task Пояса_немає_в_базі_жовтий_із_позначкою_невідомого_зсуву()
    {
        var provider = new FixedTimeZoneOffsetProvider(
            zone => string.Equals(zone, "Asia/Aqtau", StringComparison.Ordinal)
                ? null
                : KazakhstanTimeZoneReference.ExpectedOffset);

        var result = await CheckAsync(provider);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal("Asia/Aqtau=?", result.Data["staleZones"]);
    }

    /// <remarks>
    /// ⛔ Жодного <c>Unhealthy</c> і жодного винятку назовні: перевірка має тег
    /// <c>ready</c>, 503 зняв би з ротації сервер, що приймає дані, через те, що ОС
    /// не оновлювали.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "F-4")]
    public async Task Збій_самого_провайдера_жовтий_а_не_виняток()
    {
        var provider = Substitute.For<ITimeZoneOffsetProvider>();
        provider.GetUtcOffset(Arg.Any<string>(), Arg.Any<DateTime>())
            .Returns(_ => throw new InvalidOperationException("tz down"));

        var result = await CheckAsync(provider);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "F-4")]
    public async Task Текст_береться_з_каталогу_мовою_користувача_із_підстановкою_поясів()
    {
        var catalog = new FakeUiStringCatalog()
            .Add("ru", "health.tzdata.stale", "УСТАРЕЛО: {zones}");
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("ru");

        var result = await new TimeZoneDatabaseHealthCheck(FixedTimeZoneOffsetProvider.StaleAlmaty, catalog, user)
            .CheckHealthAsync(Context, CancellationToken.None);

        Assert.Equal("УСТАРЕЛО: Asia/Almaty=+06:00", result.Description);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "F-4")]
    public void Провайдер_ОС_повертає_null_для_невідомого_поясу_і_нуль_для_UTC()
    {
        var provider = new SystemTimeZoneOffsetProvider();

        Assert.Null(provider.GetUtcOffset("Asia/Atlantis", KazakhstanTimeZoneReference.CheckedAtUtc));
        Assert.Equal(
            TimeSpan.Zero,
            provider.GetUtcOffset("UTC", KazakhstanTimeZoneReference.CheckedAtUtc));
    }

    private static Task<HealthCheckResult> CheckAsync(ITimeZoneOffsetProvider provider)
    {
        var user = Substitute.For<ICurrentUser>();
        user.Language.Returns("en");

        return new TimeZoneDatabaseHealthCheck(provider, new FakeUiStringCatalog(), user)
            .CheckHealthAsync(Context, CancellationToken.None);
    }
}
