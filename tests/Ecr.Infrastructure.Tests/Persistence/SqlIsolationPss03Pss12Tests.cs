using System.Reflection;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// PSS-03 (<c>Application Name</c> з'єднань Api/воркера) і PSS-12 (Api не повторює тайм-аут команди).
/// Без SQL Server: з'єднання не відкривається.
/// </summary>
/// <remarks>
/// Мутації: прибрати рядок <c>WithDefaultApplicationName</c> у <c>AddEcrInfrastructure</c> або передати воркеру
/// ім'я Api — червоні тести імені; замінити Api-стратегію на <c>EnableRetryOnFailure</c> або прибрати
/// <c>-2</c> з перевірки в <c>ApiSqlRetryingExecutionStrategy</c> — червоні тести стратегії.
/// </remarks>
public sealed class SqlIsolationPss03Pss12Tests
{
    private const string Connection = "Server=localhost;Database=EcrUnused;Integrated Security=true;TrustServerCertificate=True";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Ім_я_застосунку_за_замовчуванням_ставиться_а_задане_адміном_не_чіпається()
    {
        var defaulted = new SqlConnectionStringBuilder(DependencyInjection.WithDefaultApplicationName(Connection, "Ecr.Api"));
        Assert.Equal("Ecr.Api", defaulted.ApplicationName);
        Assert.Equal("EcrUnused", defaulted.InitialCatalog);

        const string explicitName = Connection + ";Application Name=MyEcr";
        Assert.Same(explicitName, DependencyInjection.WithDefaultApplicationName(explicitName, "Ecr.Api"));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null, "Ecr.Api")]
    [InlineData(DependencyInjection.WorkerChildApplicationName, "Ecr.Worker.Child")]
    public void Склад_інфраструктури_кладе_ім_я_процесу_в_рядок_підключення(string? name, string expected)
    {
        using var provider = Build(name);
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        Assert.Equal(expected, new SqlConnectionStringBuilder(db.Database.GetConnectionString()).ApplicationName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Api_не_повторює_тайм_аут_команди_і_memory_grant_але_повторює_deadlock()
    {
        var strategy = Strategy(Build(null));

        Assert.IsType<ApiSqlRetryingExecutionStrategy>(strategy);
        var api = (ApiSqlRetryingExecutionStrategy)strategy;
        Assert.False(api.Retries(Sql(-2)));
        Assert.False(api.Retries(Sql(8645)));
        Assert.False(api.Retries(new TimeoutException()));
        Assert.True(api.Retries(Sql(1205)));
        Assert.True(api.Retries(Sql(40613)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Воркер_лишає_стандартну_стратегію_яка_повторює_8645()
    {
        // ⚠ Передумова PSS-12 ПЕРЕВІРЕНА (EF 10.0.13): стандартна стратегія НЕ повторює -2 (команда-тайм-аут SqlClient),
        // але повторює 8645 (тайм-аут memory grant на сервері) і голий TimeoutException — саме їх Api тепер відсікає.
        // Тест також фіксує «воркер без змін».
        var strategy = Strategy(Build(DependencyInjection.WorkerChildApplicationName));

        Assert.IsNotType<ApiSqlRetryingExecutionStrategy>(strategy);
        Assert.IsType<SqlServerRetryingExecutionStrategy>(strategy);
        var probe = typeof(SqlServerRetryingExecutionStrategy)
            .GetMethod("ShouldRetryOn", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)probe.Invoke(strategy, [Sql(1205)])!, "sanity: deadlock");
        Assert.False((bool)probe.Invoke(strategy, [Sql(-2)])!, "premise: -2 стандартна не повторює");
        Assert.True((bool)probe.Invoke(strategy, [Sql(8645)])!, "premise 8645");
        Assert.True((bool)probe.Invoke(strategy, [new TimeoutException()])!, "premise TimeoutException");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(true, 15, true)]    // Enterprise
    [InlineData(false, 16, false)]  // Standard 2022
    [InlineData(false, 17, true)]   // Standard 2025+ (не перевірено на цільовій збірці)
    public void Resource_Governor_доступний_в_Enterprise_і_у_Standard_2025(bool enterprise, int major, bool expected)
        => Assert.Equal(expected, Ecr.Infrastructure.Startup.SqlCapabilitiesProbe.ResourceGovernorSupported(enterprise, major));

    private static ServiceProvider Build(string? applicationName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = Connection })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (applicationName is null)
        {
            services.AddEcrInfrastructure(configuration);
        }
        else
        {
            services.AddEcrInfrastructure(configuration, applicationName);
        }

        return services.BuildServiceProvider();
    }

    private static IExecutionStrategy Strategy(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<EcrDbContext>().Database.CreateExecutionStrategy();
    }

    private static SqlException Sql(int number)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select(p => p.Name == "infoNumber" ? number : p.ParameterType == typeof(string) ? "test"
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException"
                        && m.GetParameters().Select(p => p.ParameterType).SequenceEqual([typeof(SqlErrorCollection), typeof(string)]));
        return (SqlException)create.Invoke(null, [errors, "16.0"])!;
    }
}
