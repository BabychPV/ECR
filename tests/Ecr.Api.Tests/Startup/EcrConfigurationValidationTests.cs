// tests/Ecr.Api.Tests/Startup/EcrConfigurationValidationTests.cs

using System.Text.Json;
using Ecr.Api.Options;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests.Startup;

/// <summary>
/// Недійсна конфігурація зупиняє старт із назвою ключа (<c>U19</c>) і не
/// замінюється дефолтом мовчки; непорожній OTLP-ендпоінт дає попередження (<c>U17</c>).
/// </summary>
[Collection("SqlServer")]
public sealed class EcrConfigurationValidationTests(SqlServerFixture sql)
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    [InlineData("Database:CommandTimeoutSeconds", "60s")]
    [InlineData("Database:BulkBatchSize", "0")]
    [InlineData("Cache:MetadataSlidingMinutes", "-5")]
    [InlineData("Auth:StampCacheSeconds", "5 сек")]
    [InlineData("Jobs:ShutdownTimeoutSeconds", "0")]
    [InlineData("Auth:RequireHttps", "yes")]
    [InlineData("Jobs:NightlyRecalculation:Enabled", "1")]
    [InlineData("Database:EditionMode", "Enterprse")]
    [InlineData("Database:EditionMode", "2")]
    [InlineData("Schema:StartupMode", "Migarte")]
    public void Недійсне_значення_називає_ключ(string key, string value)
    {
        var problems = EcrConfigurationValidation.Validate(Config((key, value)));

        var problem = Assert.Single(problems);
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);
        Assert.Contains("ECR_" + key.Replace(":", "__", StringComparison.Ordinal), problem, StringComparison.Ordinal);
    }

    /// <remarks>
    /// ⛔ ФВ-9.8 (D-205): нуль чи від'ємний ліміт перерахунку не «вимикає» межу, а
    /// зупиняє старт. <c>-1</c> у <c>MaxDegreeOfParallelism</c> означав би «без межі»
    /// — рівно те, від чого ліміт існує. Одиниця — найменше допустиме, і вона проходить.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-9.8")]
    [InlineData("Calculations:MaxParallelism", "0")]
    [InlineData("Calculations:MaxParallelism", "-1")]
    [InlineData("Calculations:MaxParallelism", "4 потоки")]
    [InlineData("Calculations:MaxInputCellsPerBinding", "0")]
    [InlineData("Calculations:MaxInputCellsPerBinding", "300k")]
    public void Недійсний_ліміт_перерахунку_зупиняє_старт(string key, string value)
    {
        var problem = Assert.Single(EcrConfigurationValidation.Validate(Config((key, value))));
        Assert.StartsWith(key + " = «" + value + "»", problem, StringComparison.Ordinal);

        Assert.Empty(EcrConfigurationValidation.Validate(Config((key, "1"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    public void Файли_налаштувань_самі_проходять_перевірку_а_порожнє_значення_це_не_задано()
    {
        // ⛔ Без цього тесту перевірка, що червоніє на ВСЬОМУ, пройшла б теорію вище —
        // і зупинила б кожну службу на першому ж старті.
        var root = RepositoryRoot();
        var shipped = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.json"), optional: false)
            .Build();
        var development = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(root, "src", "Ecr.Api", "appsettings.Development.json"), optional: false)
            .Build();

        Assert.Empty(EcrConfigurationValidation.Validate(shipped));
        Assert.Empty(EcrConfigurationValidation.Validate(development));
        Assert.Empty(EcrConfigurationValidation.Validate(Config(("Database:EditionMode", ""), ("Auth:SlidingHours", "  "))));
        Assert.Empty(EcrConfigurationValidation.Validate(Config(("Database:EditionMode", "enterprise"), ("Schema:StartupMode", "migrate"))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U19")]
    public void Кожен_числовий_і_булевий_ключ_appsettings_перевіряється()
    {
        // ⚠ Перелік у перевірці — ручний; цей тест не дає йому відстати від файлу:
        // новий числовий ключ без правила означав би рівно ту тиху підміну дефолтом,
        // яку перевірка прибрала.
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "appsettings.json")));

        var numbers = new List<string>();
        var booleans = new List<string>();
        Walk(document.RootElement, prefix: null);

        var checkedIntegers = EcrConfigurationValidation.Integers.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var checkedBooleans = EcrConfigurationValidation.Booleans.ToHashSet(StringComparer.Ordinal);

        Assert.True(numbers.Count >= 15, $"Розбір файлу зламався: числових ключів лише {numbers.Count}.");
        var withoutRule = numbers.Where(k => !checkedIntegers.Contains(k))
            .Concat(booleans.Where(k => !checkedBooleans.Contains(k)))
            .ToList();
        Assert.True(
            withoutRule.Count == 0,
            "Ключі appsettings.json без правила в EcrConfigurationValidation: " + string.Join(", ", withoutRule));

        void Walk(JsonElement element, string? prefix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        Walk(property.Value, prefix is null ? property.Name : $"{prefix}:{property.Name}");
                    }

                    break;
                case JsonValueKind.Number:
                    numbers.Add(prefix!);
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    booleans.Add(prefix!);
                    break;
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "U17")]
    public void Непорожній_OtlpEndpoint_попереджає_що_експорту_немає()
    {
        Assert.Empty(EcrConfigurationValidation.Warnings(Config(("Telemetry:OtlpEndpoint", ""))));

        var warning = Assert.Single(EcrConfigurationValidation.Warnings(
            Config(("Telemetry:OtlpEndpoint", "http://collector:4317"))));
        Assert.Contains("Telemetry:OtlpEndpoint", warning, StringComparison.Ordinal);
        Assert.Contains("ігнорується", warning, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "U19")]
    public void Застосунок_з_недійсним_ключем_не_стартує_і_пише_причину_в_журнал()
    {
        // ⛔ Саме справжній Program.cs: юніт-тести вище доводять правила, а цей —
        // що перевірку взагалі викликають на старті.
        var app = new EcrApiFactory(sql);
        var broken = app.WithWebHostBuilder(b => b.UseSetting("Database:EditionMode", "Enterprse"));
        Exception? failure;
        try
        {
            failure = Record.Exception(() => broken.CreateClient().Dispose());
        }
        finally
        {
            // Хост, що не стартував, може кинути й на звільненні — не предмет тесту.
            _ = Record.Exception(broken.Dispose);
            _ = Record.Exception(app.Dispose);
        }

        Assert.True(failure is not null, "Застосунок стартував із недійсним Database:EditionMode.");
        var message = Flatten(failure!);
        Assert.Contains("Database:EditionMode = «Enterprse»", message, StringComparison.Ordinal);
        Assert.Contains(app.ServerErrors, l => l.Contains("Database:EditionMode = «Enterprse»", StringComparison.Ordinal));

        // ⚠ Зупинка — ДО бази: інакше «fail-fast» означав би хвилину спроб з'єднання.
        Assert.DoesNotContain(app.ServerLog, l => l.Contains("Старт: база доступна", StringComparison.Ordinal));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static string Flatten(Exception root)
    {
        var parts = new List<string>();
        var stack = new Stack<Exception>([root]);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            parts.Add(current.Message);

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                stack.Push(inner);
            }
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"Ecr.sln не знайдено від {AppContext.BaseDirectory}.");
    }
}
