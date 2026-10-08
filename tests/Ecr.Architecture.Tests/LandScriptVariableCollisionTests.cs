using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож <c>tools/land/*.ps1</c> (R15b-2): локальна змінна, що збігається з ПАРАМЕТРОМ скрипта
/// (PowerShell регістронезалежний), мовчки перетирає параметр. Так `$dryRun = Invoke-Ecr …` стала
/// значенням `[switch]$DryRun` і впала з «Cannot convert PSCustomObject to SwitchParameter» ПІСЛЯ
/// публікації версії.
/// </summary>
public sealed class LandScriptVariableCollisionTests
{
    /// <summary>Свідоме перевизначення значення параметра тим самим типом (нормалізація/дефолт).</summary>
    private static readonly HashSet<string> Intentional = new(StringComparer.OrdinalIgnoreCase)
    {
        "Apply-ContractHeader.ps1:DataDir", "Apply-ContractHeader.ps1:NewVersion",
        "Import-ContractDictionaries.ps1:DataDir", "EcrApi.ps1:User",
    };

    private const string Scan = """
        $ErrorActionPreference = 'Stop'
        foreach ($f in Get-ChildItem -LiteralPath $args[0] -Filter *.ps1) {
          $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$null)
          $params = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.ParamBlockAst] }, $true) |
            ForEach-Object { $_.Parameters } | ForEach-Object { $_.Name.VariablePath.UserPath })
          $names = @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true) |
            ForEach-Object { $_.Left.FindAll({ $args[0] -is [System.Management.Automation.Language.VariableExpressionAst] }, $true) } |
            ForEach-Object { $_.VariablePath.UserPath })
          $names += @($ast.FindAll({ $args[0] -is [System.Management.Automation.Language.ForEachStatementAst] }, $true) |
            ForEach-Object { $_.Variable.VariablePath.UserPath })
          $names | Sort-Object -Unique | Where-Object { $params -contains $_ } | ForEach-Object { "$($f.Name):$_" }
        }
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-R15b-2")]
    public void Локальні_змінні_tools_land_не_збігаються_з_параметрами_скриптів()
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "& { " + Scan + " }", Path.Combine(SourceTree.Root, "tools", "land"),
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"Сканування впало: {stderr}");

        var collisions = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !Intentional.Contains(line))
            .ToList();
        Assert.True(
            collisions.Count == 0,
            "Локальна змінна збігається з параметром скрипта (регістронезалежно): " + string.Join(", ", collisions));
    }
}
