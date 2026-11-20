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
        Assert.Equal(TimeSpan.FromHours(1), options.NightWindowStart);
        Assert.Equal(TimeSpan.FromHours(5), options.NightWindowEnd);
        Assert.False(options.NightWindowInvalid);
        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("01:00-05:00", "02:30", true)]
    [InlineData("01:00-05:00", "05:00", false)]
    [InlineData("01:00-05:00", "00:59", false)]
    [InlineData("22:00-04:00", "23:30", true)]
    [InlineData("22:00-04:00", "03:59", true)]
    [InlineData("22:00-04:00", "12:00", false)]
    public void Нічне_вікно_розбирається_і_може_переходити_через_північ(string window, string at, bool inside)
    {
        var options = SupersededPurgeOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SupersededPurgeOptions.NightWindowKey] = window })
            .Build());

        Assert.False(options.NightWindowInvalid);
        Assert.Equal(inside, options.InNightWindow(TimeSpan.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData("1-5")]
    [InlineData("01:00")]
    [InlineData("03:00-03:00")]
    [InlineData("25:00-04:00")]
    public void Недійсне_нічне_вікно_дає_дефолт_і_ознаку(string window)
    {
        var options = SupersededPurgeOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [SupersededPurgeOptions.NightWindowKey] = window })
            .Build());

        Assert.True(options.NightWindowInvalid);
        Assert.Equal(SupersededPurgeOptions.DefaultNightWindowStart, options.NightWindowStart);
        Assert.Equal(SupersededPurgeOptions.DefaultNightWindowEnd, options.NightWindowEnd);
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
