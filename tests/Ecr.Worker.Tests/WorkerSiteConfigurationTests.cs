// tests/Ecr.Worker.Tests/WorkerSiteConfigurationTests.cs

using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// R5-U1/U1-07 (аудит 2026-10-09): воркер читає той самий файл майданчика
/// (<c>%ProgramData%\ECR\config\appsettings.Production.json</c>), що й Api, і перевіряє на старті ключі,
/// які читає складання дочірнього (<c>Database:*</c>, <c>Calculations:*</c>), — як U19 в Api.
/// </summary>
/// <remarks>
/// Без бази й без справжнього <c>%ProgramData%</c>: корінь — тимчасова тека.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class WorkerSiteConfigurationTests
{
    /// <remarks>
    /// Мутації (CI): прибрати файл майданчика з <c>CreateBuilder</c> → червоний (300 не дійде, лишиться 60);
    /// додати його ПЕРЕД <c>worker.settings.json</c> → червоний (файл воркера перекрив би майданчик).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "U1-07")]
    public void Ключі_файлу_майданчика_доходять_до_воркера_поверх_його_власного_файлу()
    {
        var root = Directory.CreateTempSubdirectory("ecr-worker-site-").FullName;
        try
        {
            var content = Path.Combine(root, "app");
            var programData = Path.Combine(root, "programdata");
            Directory.CreateDirectory(content);
            Directory.CreateDirectory(Path.Combine(programData, "ECR", "config"));
            File.WriteAllText(
                Path.Combine(content, WorkerProgram.SettingsFile),
                """{ "Database": { "CommandTimeoutSeconds": 60, "BulkBatchSize": 7 } }""");
            File.WriteAllText(
                WorkerProgram.SiteSettingsPath(programData),
                """{ "Database": { "CommandTimeoutSeconds": 300 }, "Calculations": { "MaxParallelism": 2 } }""");

            var builder = WorkerProgram.CreateBuilder(stub: null, contentRoot: content, commonApplicationData: programData);

            Assert.Equal("300", builder.Configuration["Database:CommandTimeoutSeconds"]);
            Assert.Equal("2", builder.Configuration["Calculations:MaxParallelism"]);
            Assert.Equal("7", builder.Configuration["Database:BulkBatchSize"]);   // файл воркера лишається основою
            Assert.Empty(WorkerConfigurationValidation.Validate(builder.Configuration));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "U1-07")]
    public void Шлях_файлу_майданчика_той_самий_що_в_Api()
    {
        Assert.Equal(
            Path.Combine("X", "ECR", "config", "appsettings.Production.json"),
            WorkerProgram.SiteSettingsPath("X"));

        // Api складає шлях у ProgramDataConfiguration.AddProgramDataConfig — ті самі сегменти.
        var api = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "Startup", "ProgramDataConfiguration.cs"));
        Assert.Contains("\"ECR\", \"config\", \"appsettings.Production.json\"", api, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Мутації (CI): прибрати ключ із переліку → червоний відповідний рядок; приймати нуль у
    /// <c>MaxParallelism</c> → червоний.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "U1-07")]
    [InlineData("Database:CommandTimeoutSeconds", "60s")]
    [InlineData("Database:CommandTimeoutSeconds", "-1")]
    [InlineData("Database:BulkBatchSize", "0")]
    [InlineData("Database:SheetLockTimeoutSeconds", "abc")]
    [InlineData("Database:MaxPoolSize", "0")]
    [InlineData("Calculations:MaxParallelism", "0")]
    [InlineData("Calculations:MaxParallelism", "-1")]
    [InlineData("Calculations:MaxInputCellsPerBinding", "0")]
    [InlineData("Database:EditionMode", "Enterprse")]
    public void Недійсне_значення_ключа_дочірнього_зупиняє_воркер_з_іменем_ключа(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var problem = Assert.Single(WorkerConfigurationValidation.Validate(configuration));
        Assert.Contains(key, problem, StringComparison.Ordinal);
        Assert.Contains("ECR_" + key.Replace(":", "__", StringComparison.Ordinal), problem, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "U1-07")]
    [InlineData("Database:CommandTimeoutSeconds", "0")]
    [InlineData("Database:CommandTimeoutSeconds", "")]
    [InlineData("Calculations:MaxParallelism", " 4 ")]
    [InlineData("Database:EditionMode", "enterprise")]
    public void Дійсне_чи_порожнє_значення_приймається(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Empty(WorkerConfigurationValidation.Validate(configuration));
    }

    /// <remarks>
    /// Межі воркера — ті самі, що в переліку U19 Api: ключ із тією самою мінімальною межею є в
    /// <c>EcrConfigurationValidation.Integers</c> (звірка по тексту — Api воркер не посилає).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "U1-07")]
    public void Межі_воркера_збігаються_з_переліком_Api()
    {
        var api = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Ecr.Api", "Options", "EcrConfigurationValidation.cs"));

        Assert.NotEmpty(WorkerConfigurationValidation.Integers);
        foreach (var (key, min) in WorkerConfigurationValidation.Integers)
        {
            Assert.Contains($"(\"{key}\", {min})", api, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "Ecr.Api")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
