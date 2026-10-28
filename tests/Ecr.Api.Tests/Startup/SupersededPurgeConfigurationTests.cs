// tests/Ecr.Api.Tests/Startup/SupersededPurgeConfigurationTests.cs
using Ecr.Api.Options;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Q-17: ключі <c>Calculations:SupersededPurge:*</c> - у файлі прибирання й видалення вимкнені (незворотна зміна
/// даних вмикається словом людини), недійсне значення зупиняє старт з назвою ключа.
/// </summary>
public sealed class SupersededPurgeConfigurationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Типовий_файл_лишає_прибирання_і_видалення_вимкненими()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();

        var options = SupersededPurgeOptions.Read(shipped);

        // Мутація: поставити Enabled/DeleteEnabled=true у appsettings.json - червоніє.
        Assert.False(options.Enabled);
        Assert.False(options.DeleteEnabled);
        Assert.Equal(500, options.BatchSize);
        Assert.Equal(2000, options.MaxBatchesPerRun);
        Assert.True(options.IncludeTrace);
        Assert.False(options.RebuildAfter);
        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("Calculations:SupersededPurge:BatchSize", "0")]
    [InlineData("Calculations:SupersededPurge:MaxBatchesPerRun", "0")]
    [InlineData("Calculations:SupersededPurge:PauseMilliseconds", "-1")]
    [InlineData("Calculations:SupersededPurge:LogPauseThresholdPercent", "0")]
    [InlineData("Calculations:SupersededPurge:Enabled", "1")]
    [InlineData("Calculations:SupersededPurge:DeleteEnabled", "yes")]
    [InlineData("Calculations:SupersededPurge:IncludeTrace", "так")]
    public void Недійсне_значення_називає_ключ(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var problem = Assert.Single(EcrConfigurationValidation.Validate(configuration));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Корінь репозиторію (Ecr.sln) не знайдено.");
    }
}
