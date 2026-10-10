using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// S2-07 (аудит 2026-10-09b): <c>-ConfigValues</c> пишеться в <c>appsettings.Production.json</c>, який читає кожен
/// локальний користувач, а правило «секрети ніколи у файлі» (D-11) не примушувалось. Тепер розділи
/// <c>ConnectionStrings</c> і <c>Secrets</c> у файлі — відмова до будь-якої зміни.
/// </summary>
/// <remarks>
/// Предмет — справжня функція скрипта (<see cref="DeployScriptHarness"/>); файлової системи й msiexec тут немає.
/// Тести/мутація — CI, локально не запускались.
/// </remarks>
public sealed class DeployConfigValuesTests
{
    private const string Body = """
        function Out-Cfg([string] $key, [string] $json) {
            $p = Get-ConfigValuesProblem -Values ($json | ConvertFrom-Json)
            "cfg.$key.ok=$($null -eq $p)"
            "cfg.$key.names=$([bool] ($p -and ($p.Contains('ConnectionStrings') -or $p.Contains('Secrets'))))"
        }
        Out-Cfg 'empty'     '{}'
        Out-Cfg 'logging'   '{"Logging":{"File":{"Directory":"D:/logs"}}}'
        Out-Cfg 'secrets'   '{"Secrets":{"PiAf.Primary":"x"}}'
        Out-Cfg 'conn'      '{"ConnectionStrings":{"Ecr":"Server=x"}}'
        Out-Cfg 'caseless'  '{"connectionstrings":{"Ecr":"x"}}'
        Out-Cfg 'flat'      '{"Secrets:PiAf.Primary":"x"}'
        Out-Cfg 'mixed'     '{"Logging":{"Level":"Information"},"Secrets":{"a":"b"}}'
        Out-Cfg 'prefix'    '{"SecretsX":1,"MyConnectionStrings":2}'
        Out-Cfg 'array'     '[1,2]'
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-ConfigValuesProblem"], Body));

    /// <remarks>
    /// Мутації (CI): повертати <c>$null</c> завжди → червоні <c>secrets</c>/<c>conn</c>/<c>caseless</c>/<c>flat</c>/<c>mixed</c>;
    /// порівнювати з урахуванням регістру → червоний <c>caseless</c>; відмовляти за будь-яким ключем → червоні
    /// <c>empty</c>/<c>logging</c>/<c>prefix</c>.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("empty", "True")]
    [InlineData("logging", "True")]
    [InlineData("secrets", "False")]
    [InlineData("conn", "False")]
    [InlineData("caseless", "False")]
    [InlineData("flat", "False")]
    [InlineData("mixed", "False")]
    [InlineData("prefix", "True")]
    [InlineData("array", "True")]
    public void Секретні_розділи_у_ConfigValues_відхиляються(string key, string ok)
    {
        Assert.Equal(ok, DeployScriptHarness.Value(Results.Value, $"cfg.{key}.ok"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відмова_називає_знайдений_розділ()
    {
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "cfg.secrets.names"));
        Assert.Equal("True", DeployScriptHarness.Value(Results.Value, "cfg.conn.names"));
    }

    /// <remarks>
    /// ⛔ Порядок у тексті скрипта: вердикт — ДО кроку 2 (схема) і кроку 3 (MSI), тобто до будь-якої зміни.
    /// Мутація: перенести перевірку в крок 5 (де файл пишеться) або прибрати → червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перевірка_ConfigValues_стоїть_до_змін()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        var verdict = script.IndexOf("Get-ConfigValuesProblem -Values", StringComparison.Ordinal);
        var step2 = script.IndexOf("Write-Step \"Крок 2/7", StringComparison.Ordinal);
        var step3 = script.IndexOf("Write-Step \"Крок 3/7: MSI\"", StringComparison.Ordinal);
        var write = script.IndexOf("записати значення з $ConfigValues", StringComparison.Ordinal);

        Assert.True(verdict > 0, "перевірки секретних розділів -ConfigValues немає");
        Assert.True(step2 > verdict && step3 > verdict && write > verdict, "-ConfigValues перевіряється після початку змін");
    }
}
