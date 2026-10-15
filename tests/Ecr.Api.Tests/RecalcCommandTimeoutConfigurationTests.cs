using Ecr.Api.Options;
using Ecr.Application.Ports;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>Database:RecalcCommandTimeoutSeconds</c> (дефолт 180, стеля перерахунку) доїжджає до контексту через
/// <c>IRecalcCommandTimeout</c>; глобальний <c>Database:CommandTimeoutSeconds</c> (60) лишається для решти.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЯ: прибрати <c>Apply()</c> (або читати ключ <c>Database:CommandTimeoutSeconds</c>) — тести
/// «дефолт»/«значення конфігурації» червоні. Без SQL Server: з'єднання не відкривається.
/// </remarks>
public sealed class RecalcCommandTimeoutConfigurationTests
{
    private const string FakeConnection = "Server=(local);Database=EcrUnused;Integrated Security=true";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_Apply_контекст_має_глобальний_таймаут_а_після_Apply_стелю_перерахунку_180()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        Assert.Equal(60, db.Database.GetCommandTimeout());

        scope.ServiceProvider.GetRequiredService<IRecalcCommandTimeout>().Apply();

        Assert.Equal(180, db.Database.GetCommandTimeout());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Стеля_перерахунку_береться_з_конфігурації_а_не_з_дефолту()
    {
        using var provider = Build(("Database:RecalcCommandTimeoutSeconds", "777"));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRecalcCommandTimeout>().Apply();

        Assert.Equal(777, scope.ServiceProvider.GetRequiredService<EcrDbContext>().Database.GetCommandTimeout());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("5", 10, 30)]
    [InlineData("999999", 60, 3600)]
    public void Значення_поза_діапазоном_затискається_в_30_3600(string configured, int global, int expected)
    {
        using var provider = Build(
            ("Database:RecalcCommandTimeoutSeconds", configured),
            ("Database:CommandTimeoutSeconds", global.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRecalcCommandTimeout>().Apply();

        Assert.Equal(expected, scope.ServiceProvider.GetRequiredService<EcrDbContext>().Database.GetCommandTimeout());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Apply_не_знижує_вищий_глобальний_таймаут()
    {
        using var provider = Build(("Database:CommandTimeoutSeconds", "900"));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IRecalcCommandTimeout>().Apply();

        Assert.Equal(900, scope.ServiceProvider.GetRequiredService<EcrDbContext>().Database.GetCommandTimeout());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    [InlineData("180s")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1")]
    [InlineData("29")]
    public void Недійсне_значення_ключа_зупиняє_старт_з_назвою_ключа(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new("Database:RecalcCommandTimeoutSeconds", value)])
            .Build();

        var problem = Assert.Single(EcrConfigurationValidation.Validate(configuration));

        Assert.StartsWith("Database:RecalcCommandTimeoutSeconds = «" + value + "»", problem, StringComparison.Ordinal);
    }

    private static ServiceProvider Build(params (string Key, string Value)[] overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json"), optional: false)
            .AddInMemoryCollection(
                overrides
                    .Select(o => new KeyValuePair<string, string?>(o.Key, o.Value))
                    .Append(new KeyValuePair<string, string?>("ConnectionStrings:Ecr", FakeConnection)))
            .Build();

        // Самоперевірка: файл прочитано (інакше «дефолт» тесту — дефолт читача, а не файлу).
        Assert.Equal("ecr.auth", configuration["Auth:CookieName"]);

        return new ServiceCollection()
            .AddEcrInfrastructure(configuration)
            .BuildServiceProvider();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Не знайдено кореня репозиторію (каталогу з src/).");
    }
}
