using Ecr.Api.Startup;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-134 для Q-213: <see cref="ProgramDataConfiguration.AddProgramDataConfig"/> —
/// без піднятого хоста й без SQL Server (на відміну від решти тестів цього
/// проєкту), бо тут перевіряється лише збірка <see cref="IConfiguration"/>.
/// </summary>
public sealed class ProgramDataConfigurationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Читає_значення_з_файлу_ProgramData()
    {
        var root = CreateConfigFile("""{ "Telemetry": { "OtlpEndpoint": "http://collector:4317" } }""");
        try
        {
            var config = new ConfigurationBuilder().AddProgramDataConfig(root).Build();

            Assert.Equal("http://collector:4317", config["Telemetry:OtlpEndpoint"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Не_падає_якщо_файлу_немає()
    {
        // dev/CI-машина: %ProgramData%\ECR\config\ там узагалі не існує.
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var config = new ConfigurationBuilder().AddProgramDataConfig(root).Build();

            Assert.Null(config["Anything"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Змінна_оточення_ECR_перекриває_файл_ProgramData()
    {
        // Той самий порядок викликів, що Program.cs (AddProgramDataConfig,
        // потім AddEnvironmentVariables(prefix: "ECR_")) — доводить D-11
        // наживо: секрет із файлу НІКОЛИ не переможе явно заданий ECR_.
        var root = CreateConfigFile("""{ "ConnectionStrings": { "Ecr": "from-file-must-lose" } }""");
        const string envVar = "ECR_ConnectionStrings__Ecr";
        Environment.SetEnvironmentVariable(envVar, "from-env-must-win");
        try
        {
            var config = new ConfigurationBuilder()
                .AddProgramDataConfig(root)
                .AddEnvironmentVariables(prefix: "ECR_")
                .Build();

            Assert.Equal("from-env-must-win", config.GetConnectionString("Ecr"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateConfigFile(string json)
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        var configDir = Path.Combine(root, "ECR", "config");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "appsettings.Production.json"), json);
        return root;
    }
}
