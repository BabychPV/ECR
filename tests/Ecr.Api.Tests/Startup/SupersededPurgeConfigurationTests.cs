// tests/Ecr.Api.Tests/Startup/SupersededPurgeConfigurationTests.cs
using Ecr.Api.Options;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Q-17 / <c>D-560</c>: ключі <c>Calculations:SupersededPurge:*</c> - прибирання й видалення УВІМКНЕНІ за
/// замовчуванням (рішення людини 20.11, PSS-01) і у файлі, і в коді (ключа немає - увімкнено), недійсне значення
/// зупиняє старт з назвою ключа.
/// </summary>
public sealed class SupersededPurgeConfigurationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Типовий_файл_вмикає_прибирання_і_видалення()
    {
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();

        var options = SupersededPurgeOptions.Read(shipped);

        // Мутація: поставити Enabled/DeleteEnabled=false у appsettings.json - червоніє.
        Assert.True(options.Enabled);
        Assert.True(options.DeleteEnabled);
        Assert.Equal(500, options.BatchSize);
        Assert.Equal(SupersededPurgeOptions.DefaultMaxBatchesPerRun, options.MaxBatchesPerRun);
        Assert.True(options.IncludeTrace);
        Assert.False(options.RebuildAfter);
        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
    }

    /// <remarks>
    /// ⛔ Служба <c>EcrWorker</c> <c>appsettings.json</c> Api не читає (<c>worker.settings.json</c> розділу
    /// <c>Calculations</c> не має), а саме вона в режимі <c>Database</c> завершує перерахунки. Ключ, якого немає, -
    /// увімкнено. Мутація: повернути <c>defaultValue: false</c> у <c>SupersededPurgeOptions.Read</c> - червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Без_ключів_прибирання_увімкнене_а_без_конфігурації_вимкнене()
    {
        var empty = SupersededPurgeOptions.Read(new ConfigurationBuilder().Build());
        Assert.True(empty.Enabled);
        Assert.True(empty.DeleteEnabled);
        Assert.True(empty.IncludeTrace);

        var explicitOff = SupersededPurgeOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SupersededPurgeOptions.EnabledKey] = "false" })
            .Build());
        Assert.False(explicitOff.Enabled);

        // Сховище, створене вручну без конфігурації (тести), - вимкнено.
        Assert.False(SupersededPurgeOptions.Read(null).Enabled);
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
