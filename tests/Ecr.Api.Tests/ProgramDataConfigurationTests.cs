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
        //
        // ⛔ Ключ — власний (ConnectionStrings:ProgramDataProbe), НЕ "Ecr":
        // клас поза колекцією "SqlServer" і йде паралельно з фабриками, а ті
        // передають справжній рядок з'єднання через ECR_ConnectionStrings__Ecr
        // змінною ПРОЦЕСУ. Спільний ключ давав гонитву: хост фабрики стартував
        // із "from-env-must-win" або (після finally) без рядка взагалі.
        // Порядок шарів не залежить від імені ключа, а секція ConnectionStrings
        // та сама — доказ не слабшає.
        var root = CreateConfigFile("""{ "ConnectionStrings": { "ProgramDataProbe": "from-file-must-lose" } }""");
        const string envVar = "ECR_ConnectionStrings__ProgramDataProbe";
        Environment.SetEnvironmentVariable(envVar, "from-env-must-win");
        try
        {
            var config = new ConfigurationBuilder()
                .AddProgramDataConfig(root)
                .AddEnvironmentVariables(prefix: "ECR_")
                .Build();

            Assert.Equal("from-env-must-win", config.GetConnectionString("ProgramDataProbe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// U1-08: синтаксична помилка у файлі на ЖИВІЙ службі (перечитування) не обнуляє налаштування — лишаються значення
    /// з останнього вдалого читання, а помилка іде в <see cref="ProgramDataConfiguration.ReloadFailed"/>; після
    /// виправлення файлу нове значення підхоплюється. Перше читання (старт) з поганим файлом — як і раніше, виняток.
    /// </summary>
    /// <remarks>
    /// ⚠ Перечитування викликається детерміновано (<c>IConfigurationRoot.Reload</c>), а не через файловий спостерігач
    /// (його затримка зробила б тест плаваючим). Мутація: повернути <c>AddJsonFile</c> замість свого постачальника —
    /// <c>Reload</c> кидає виняток, а значення зникає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void U1_08_синтаксична_помилка_при_перечитуванні_лишає_останні_вдалі_значення()
    {
        var root = CreateConfigFile("""{ "Telemetry": { "OtlpEndpoint": "http://collector:4317" } }""");
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var previousHandler = ProgramDataConfiguration.ReloadFailed;
        ProgramDataConfiguration.ReloadFailed = (file, _) => failures.Add(file);
        try
        {
            var config = (IConfigurationRoot)new ConfigurationBuilder().AddProgramDataConfig(root).Build();
            Assert.Equal("http://collector:4317", config["Telemetry:OtlpEndpoint"]);

            var path = Path.Combine(root, "ECR", "config", "appsettings.Production.json");
            File.WriteAllText(path, """{ "Telemetry": { "OtlpEndpoint": "http://collector:4317", } """);

            config.Reload();

            Assert.Equal("http://collector:4317", config["Telemetry:OtlpEndpoint"]);

            // ⚠ NotEmpty, а не Single: файловий спостерігач (reloadOnChange) теж може перечитати зіпсований файл.
            Assert.NotEmpty(failures);

            // Виправили файл — нове значення діє.
            File.WriteAllText(path, """{ "Telemetry": { "OtlpEndpoint": "http://other:4317" } }""");
            config.Reload();

            Assert.Equal("http://other:4317", config["Telemetry:OtlpEndpoint"]);
        }
        finally
        {
            ProgramDataConfiguration.ReloadFailed = previousHandler;
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void U1_08_непридатний_файл_на_старті_лишається_відмовою()
    {
        var root = CreateConfigFile("""{ "Telemetry": { "OtlpEndpoint": """);
        try
        {
            // Перше читання — не «перечитування»: служба не стартує з файлом, якого не може розібрати.
            Assert.ThrowsAny<Exception>(() => new ConfigurationBuilder().AddProgramDataConfig(root).Build());
        }
        finally
        {
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
