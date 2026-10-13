using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Приймальна RC «П», продовження <see cref="LandBuildSqlConnectionRequiredTests"/>: <c>Compare-LandParity.ps1</c> і
/// <c>Compare-LandParityFull.ps1</c> мали <c>-SqlConnection</c> із дефолтом на живий стенд (EcrLandUi), тож запуск без
/// явного параметра мовчки читав його. Тепер параметр без значення за замовчуванням; SQL-режим без нього відмовляє,
/// офлайн-режими (-EcrResultsFile / -StateFile) SQL не торкаються. Статичний сторож + живі відмови (без мережі й SQL:
/// відмова стоїть до будь-якого підключення).
/// </summary>
public sealed class LandParitySqlConnectionRequiredTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static (int Exit, string Output) Run(params string[] scriptAndArgs)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" })
        {
            start.ArgumentList.Add(a);
        }

        foreach (var a in scriptAndArgs)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 120 с.");
        }

        return (process.ExitCode, stdout + stderr);
    }

    [Theory]
    [InlineData("Compare-LandParity.ps1")]
    [InlineData("Compare-LandParityFull.ps1")]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void SqlConnection_не_має_значення_за_замовчуванням_і_літералу_живого_стенда(string name)
    {
        var text = File.ReadAllText(LandFile(name));

        Assert.DoesNotMatch(new Regex(@"\[string\]\s*\$SqlConnection\s*=", RegexOptions.CultureInvariant), text);
        Assert.DoesNotMatch(new Regex(@"Database\s*=\s*EcrLandUi", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), text);
        Assert.True(Regex.IsMatch(text, @"\[string\]\s*\$SqlConnection\s*,"), "Параметр -SqlConnection має лишитися без значення за замовчуванням.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Compare_LandParity_у_SQL_режимі_без_SqlConnection_відмовляє()
    {
        var tmp = Path.GetTempPath();
        var (exit, output) = Run(
            LandFile("Compare-LandParity.ps1"),
            "-JsonDir", Path.Combine(tmp, "ecr-no-such-json"),
            "-OracleFile", Path.Combine(tmp, "ecr-no-such-oracle.json"),
            "-OutFile", Path.Combine(tmp, $"ecr-parity-{Guid.NewGuid():N}.json"));

        Assert.NotEqual(0, exit);
        Assert.Contains("-SqlConnection", output, StringComparison.Ordinal);
        Assert.Contains("EcrLand", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Compare_LandParityFull_у_SQL_режимі_без_SqlConnection_відмовляє()
    {
        var tmp = Path.GetTempPath();
        var (exit, output) = Run(
            LandFile("Compare-LandParityFull.ps1"),
            "-JsonDir", Path.Combine(tmp, "ecr-no-such-json"),
            "-OracleTsv", Path.Combine(tmp, "ecr-no-such-oracle.tsv"),
            "-OutPrefix", Path.Combine(tmp, $"ecr-parity-{Guid.NewGuid():N}"));

        Assert.NotEqual(0, exit);
        Assert.Contains("-SqlConnection", output, StringComparison.Ordinal);
        Assert.Contains("EcrLand", output, StringComparison.Ordinal);
    }
}
