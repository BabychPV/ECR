// tests/Ecr.Api.Tests/Startup/SnapshotRetentionConfigurationTests.cs
using Ecr.Api.Options;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// AN-134: ключі <c>Retention:SubmissionSnapshots:*</c> — типові значення у файлі, видалення вимкнено,
/// недійсне значення (зокрема менше двох зрізів) зупиняє старт з назвою ключа.
/// </summary>
public sealed class SnapshotRetentionConfigurationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "AN-134")]
    public void Типовий_файл_налаштувань_лишає_видалення_вимкненим_і_п_ять_зрізів()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();

        var options = SnapshotRetentionOptions.Read(shipped);

        // ⛔ Мутація: поставити DeleteEnabled=true у appsettings.json — це червоніє: видалення вмикає лише людина.
        Assert.True(options.Enabled);
        Assert.False(options.DeleteEnabled);
        Assert.Equal(5, options.RetainSnapshots);
        Assert.Equal(50, options.BatchSize);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.PauseBetweenBatches);
        Assert.Equal(200, options.MaxBatchesPerRun);
        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "AN-134")]
    [InlineData("Retention:SubmissionSnapshots:RetainSnapshots", "1")]
    [InlineData("Retention:SubmissionSnapshots:RetainSnapshots", "0")]
    [InlineData("Retention:SubmissionSnapshots:RetainSnapshots", "п'ять")]
    [InlineData("Retention:SubmissionSnapshots:BatchSize", "0")]
    [InlineData("Retention:SubmissionSnapshots:PauseMilliseconds", "-1")]
    [InlineData("Retention:SubmissionSnapshots:MaxBatchesPerRun", "0")]
    [InlineData("Retention:SubmissionSnapshots:DeleteEnabled", "yes")]
    [InlineData("Retention:SubmissionSnapshots:Enabled", "1")]
    public void Недійсне_значення_називає_ключ(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var problem = Assert.Single(EcrConfigurationValidation.Validate(configuration));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "AN-134")]
    public void Два_зрізи_найменше_допустиме_значення()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:SubmissionSnapshots:RetainSnapshots"] = "2",
            })
            .Build();

        Assert.Empty(EcrConfigurationValidation.Validate(configuration));
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
