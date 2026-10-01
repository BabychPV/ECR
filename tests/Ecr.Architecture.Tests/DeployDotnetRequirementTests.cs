using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// <c>deploy-ecr.ps1</c> вимагає .NET SDK лише там, де його реально кличуть: <c>build-msi.ps1</c>
/// (без <c>-MsiPath</c>) і <c>dotnet ef</c> (схема не з пакета й без <c>-SkipSchema</c>).
/// </summary>
/// <remarks>
/// ⛔ Предмет. Крок 1 безумовно вимагав <c>dotnet</c> у PATH («.NET SDK не знайдено»), хоча
/// `11-install-guide.md` §2.1 обіцяє «SDK на сервері не потрібен», а пакований запуск (Q-219: `sql\` і
/// `migration.sql` поруч зі скриптом, готовий MSI) не кличе `dotnet` жодного разу — чистий сервер
/// замовника зупинявся на передумові, якої його сценарій не має. Предмет тесту — справжня функція
/// <c>Get-DotnetRequirement</c> зі скрипта.
/// </remarks>
public sealed class DeployDotnetRequirementTests
{
    private const string Body = """
        function Out-Need([string] $key, $need) {
            "dotnet.$key.required=$($need.Required)"
            "dotnet.$key.count=$(@($need.Reasons).Count)"
            "dotnet.$key.msi=$([bool] (@($need.Reasons) -match 'build-msi'))"
            "dotnet.$key.ef=$([bool] (@($need.Reasons) -match 'dotnet ef'))"
        }
        Out-Need 'packaged'    (Get-DotnetRequirement -HasMsiPath $true  -SkipSchema $false -IsPackagedSchema $true)
        Out-Need 'skipSchema'  (Get-DotnetRequirement -HasMsiPath $true  -SkipSchema $true  -IsPackagedSchema $false)
        Out-Need 'noMsi'       (Get-DotnetRequirement -HasMsiPath $false -SkipSchema $true  -IsPackagedSchema $true)
        Out-Need 'repoSchema'  (Get-DotnetRequirement -HasMsiPath $true  -SkipSchema $false -IsPackagedSchema $false)
        Out-Need 'both'        (Get-DotnetRequirement -HasMsiPath $false -SkipSchema $false -IsPackagedSchema $false)
        """;

    private static readonly Lazy<Dictionary<string, string>> Results =
        new(() => DeployScriptHarness.Run(["Get-DotnetRequirement"], Body));

    /// <remarks>
    /// ⛔ Головний випадок — чистий сервер: пакована схема + готовий MSI → SDK не потрібен.
    /// Мутація (прогнано): у функції прибрати умову <c>-not $IsPackagedSchema</c> →
    /// <c>packaged</c> вимагає SDK, а <c>ef</c> лишається лише за <c>repoSchema</c>; тест червоний.
    /// Друга мутація: додати безумовну причину → усі рядки <c>required</c> крім <c>True</c> червоні.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("packaged", "False", "0", "False", "False")]
    [InlineData("skipSchema", "False", "0", "False", "False")]
    [InlineData("noMsi", "True", "1", "True", "False")]
    [InlineData("repoSchema", "True", "1", "False", "True")]
    [InlineData("both", "True", "2", "True", "True")]
    public void Dotnet_потрібен_лише_коли_його_справді_кличуть(
        string scenario, string required, string count, string msi, string ef)
    {
        Assert.Equal(required, DeployScriptHarness.Value(Results.Value, $"dotnet.{scenario}.required"));
        Assert.Equal(count, DeployScriptHarness.Value(Results.Value, $"dotnet.{scenario}.count"));
        Assert.Equal(msi, DeployScriptHarness.Value(Results.Value, $"dotnet.{scenario}.msi"));
        Assert.Equal(ef, DeployScriptHarness.Value(Results.Value, $"dotnet.{scenario}.ef"));
    }

    /// <remarks>
    /// Рішення береться з функції, а не з безумовного <c>Get-Command dotnet</c> у кроці 1: поки
    /// рядок повертається — функція нічого не вирішує.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Крок_1_не_вимагає_dotnet_безумовно()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "deploy-ecr.ps1"));

        Assert.DoesNotContain(
            "if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {",
            script,
            StringComparison.Ordinal);
        Assert.Contains("$dotnetNeed.Required -and -not (Get-Command dotnet", script, StringComparison.Ordinal);
    }
}
