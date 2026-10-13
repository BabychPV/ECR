using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RC E, верифікація 13.10, дефекти D1/D2 у <c>tools/land/Build-LandTemplate.ps1</c>:
/// D1 - етап LandParity мусить іти ПЕРЕД Methodologies (прив'язки LP-08, вимкнені теж, валять R16-4
/// <c>categoryRuleUnknownArgument</c> на PUT category-rule); D2 - simulate приймає <c>periodKey</c> числом
/// (<c>SimulateMethodologyRequest(int, int PeriodKey)</c>), а не <c>{ value = ... }</c>.
/// Етап <c>Stage-LandParity</c> виконується справжнім PowerShell із підставленими Invoke-Ecr/Api.
/// </summary>
public sealed class LandBuildStageFixesTests
{
    private static string BuildScript => Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1");

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        $script = $args[0]; $mode = $args[1]; $root = $args[2]
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-LandParity' }, $true)
        if (-not $fn) { throw 'Stage-LandParity не знайдено' }
        $here = Join-Path $root 'tools\land'
        $repo = $root
        $Year = 2026
        function Log([string]$m) { Write-Host ('LOG=' + $m) }
        function LoginAs([string]$u) { }
        function Get-EcrErrorCode($r) { return 'ECR-CALC-0422' }
        function Items($j) {
            if ($null -eq $j) { return @() }
            if ($j -is [array]) { return $j }
            if ($j.PSObject.Properties['items']) { return @($j.items) }
            return @($j)
        }
        function Api {
            param([string]$Method, [string]$Path, $Payload = $null)
            if ($Path -eq '/api/v1/methodologies') { return @{ Json = @(@{ code = 'ECW_C11_04'; id = 7 }) } }
            if ($Path -like '*/versions/70/constants') { return @{ Json = @() } }
            if ($Path -like '*/versions/70/tests') { return @{ Json = @(@{ code = 'PARITY_1'; inputJson = '{"documentId":1,"periodKey":{"value":202606},"arguments":[]}'; expectedJson = '{}'; tolerance = '0.001' }) } }
            if ($Path -like '*/methodologies/7/versions') { return @{ Json = @(@{ id = 70; status = 'Draft' }) } }
            throw "Api: неочікуваний шлях $Method $Path"
        }
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null)
            if ($Method -eq 'GET' -and $Path -like '*/registries/*') { return @{ Status = 404; Text = ''; Json = $null } }
            if ($Method -eq 'GET' -and $Path -like '*/category-rule') { return @{ Status = 404; Text = ''; Json = $null } }
            if ($Method -eq 'PUT' -and $Path -like '*/category-rule') {
                if ($mode -eq 'bound') { return @{ Status = 422; Text = '{"messageKey":"err.ECR-CALC-0422.categoryRuleUnknownArgument"}'; Json = $null } }
                return @{ Status = 200; Text = ''; Json = $null }
            }
            if ($Method -eq 'PUT' -and $Path -like '*/tests/PARITY_1') { return @{ Status = 200; Text = ''; Json = $null } }
            if ($Method -eq 'POST' -and $Path -like '*/simulate') {
                Write-Host ('SIM=' +($Body | ConvertTo-Json -Depth 5 -Compress))
                return @{ Status = 200; Text = ''; Json = [pscustomobject]@{ isGreen = $true } }
            }
            throw "Invoke-Ecr: неочікуваний виклик $Method $Path"
        }
        . ([scriptblock]::Create($fn.Extent.Text))
        Stage-LandParity
        """;

    private static (int Exit, string Output) RunStage(string mode)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-stage-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
                         BuildScript, mode, SourceTree.Root,
                     })
            {
                start.ArgumentList.Add(argument);
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
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void D2_simulate_шле_periodKey_числом_а_не_об_єктом_value()
    {
        var (exit, output) = RunStage("free");

        Assert.True(exit == 0, output);
        var sim = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("SIM=", StringComparison.Ordinal));
        Assert.NotNull(sim);
        Assert.Contains("\"periodKey\":202606", sim, StringComparison.Ordinal);
        Assert.Contains("\"methodologyVersionId\":70", sim, StringComparison.Ordinal);
        Assert.DoesNotContain("\"value\"", sim, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void D2_у_тексті_скрипта_simulate_без_periodKey_value()
    {
        var text = File.ReadAllText(BuildScript);

        var simulateLines = text.Split('\n').Where(l => l.Contains("/simulate\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(simulateLines);
        Assert.All(simulateLines, l => Assert.DoesNotContain("periodKey = @{", l, StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void D1_LandParity_виконується_перед_Methodologies_у_диспетчері_етапів()
    {
        var text = File.ReadAllText(BuildScript);

        var parity = Regex.Match(text, @"if \(\$Stage -contains 'LandParity'\) \{ Stage-LandParity \}");
        var methodologies = Regex.Match(text, @"if \(Want 'Methodologies'\) \{ Stage-Methodologies \}");
        Assert.True(parity.Success, "Виклик Stage-LandParity не знайдено.");
        Assert.True(methodologies.Success, "Виклик Stage-Methodologies не знайдено.");
        Assert.True(parity.Index < methodologies.Index, "LandParity має виконуватись ДО Methodologies (D1).");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void D1_відмова_unknown_argument_друкує_підказку_про_порядок_етапів()
    {
        var (exit, output) = RunStage("bound");

        Assert.True(exit == 0, output);
        Assert.Contains("categoryRuleUnknownArgument", File.ReadAllText(BuildScript), StringComparison.Ordinal);
        Assert.Contains("tools/land/README.md", output, StringComparison.Ordinal);
        Assert.DoesNotContain("SIM=", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void D1_README_описує_порядок_LandParity_до_Methodologies()
    {
        var readme = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "README.md"));

        Assert.Contains("LandParity ДО Methodologies", readme, StringComparison.Ordinal);
        Assert.Contains("categoryRuleUnknownArgument", readme, StringComparison.Ordinal);
    }
}
