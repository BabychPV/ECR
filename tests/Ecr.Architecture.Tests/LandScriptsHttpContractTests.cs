using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Сторож <c>tools/land</c>: логіка, що перевіряється без виклику API. Скрипти парсяться AST PowerShell;
/// решта — текстові інваріанти (loopback-cookie для http, міграція при 0 змін, діагностика ECR-TMPL-4228).
/// </summary>
public sealed class LandScriptsHttpContractTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private const string Scan = """
        $ErrorActionPreference = 'Stop'
        foreach ($f in Get-ChildItem -LiteralPath $args[0] -Filter *.ps1) {
          $errs = $null
          $ast = [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errs)
          foreach ($e in $errs) { "ERR:$($f.Name):$($e.Message)" }
          foreach ($fn in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { "FN:$($f.Name):$($fn.Name)" }
          foreach ($a in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] }, $true)) {
            foreach ($v in $a.Left.FindAll({ $args[0] -is [System.Management.Automation.Language.VariableExpressionAst] }, $true)) { "ASG:$($f.Name):$($v.VariablePath.UserPath)" }
          }
        }
        """;

    private static List<string> RunScan()
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
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипти_tools_land_парсяться_без_помилок_а_змінної_dryRun_і_loopback_функції_є()
    {
        var lines = RunScan();

        Assert.DoesNotContain(lines, l => l.StartsWith("ERR:", StringComparison.Ordinal));
        // `$dryRun` — назва, що збігається з параметром [switch]$DryRun (регістронезалежно).
        Assert.DoesNotContain(lines, l => l.StartsWith("ASG:", StringComparison.Ordinal) &&
            l.EndsWith(":dryRun", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("FN:EcrApi.ps1:Get-LoopbackFlag", lines);
        Assert.Contains("FN:EcrApi.ps1:Enable-LoopbackHttpCookies", lines);
        Assert.Contains("FN:EcrApi.ps1:Wait-EcrJob", lines);
        Assert.Contains("FN:Apply-ContractHeader.ps1:Invoke-DocumentMigration", lines);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Http_дозволено_лише_для_loopback_і_cookie_копіюється_без_Secure()
    {
        var api = File.ReadAllText(LandFile("EcrApi.ps1"));

        Assert.Matches(@"Scheme -eq 'http' -and -not \(Get-LoopbackFlag", api);
        Assert.Contains("$copy.Secure = $false", api);
        Assert.Contains("IsLoopback", api);
        // Відмова йде ДО входу: пароль по відкритому http не відправляється.
        Assert.True(
            api.IndexOf("використовуйте https", StringComparison.Ordinal) <
            api.IndexOf("'/api/v1/login/local'", StringComparison.Ordinal),
            "Перевірка loopback має бути до POST /login/local.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Міграція_документів_виконується_і_коли_змін_шапки_немає_і_ECR_TMPL_4228_має_діагностику()
    {
        var apply = File.ReadAllText(LandFile("Apply-ContractHeader.ps1"));

        var zeroChanges = Regex.Match(apply, @"if \(\$todo\.Count -eq 0 -and \$null -eq \$draft\) \{(?<body>.*?)\r?\n\}\r?\n", RegexOptions.Singleline);
        Assert.True(zeroChanges.Success, "Гілка «0 змін» не знайдена.");
        Assert.Contains("Invoke-DocumentMigration", zeroChanges.Groups["body"].Value);
        Assert.Contains("$hasMigration", zeroChanges.Groups["body"].Value);
        // @($null).Count == 1: порожній -MigrateDocumentId не має вважатися «є документи».
        Assert.Matches(@"\$hasMigration = @\(\$MigrateDocumentId \| Where-Object", apply);

        Assert.Contains("Test-EcrTemplateUnsuitable $pub", apply);
        Assert.Contains("Test-EcrTemplateUnsuitable $cl", apply);
        Assert.Contains("Stop-EcrTemplateUnsuitable", apply);
        Assert.Contains("ECR-TMPL-4228", File.ReadAllText(LandFile("EcrApi.ps1")));
        // 202 + jobId обробляється, `async` без -Async не надсилається.
        Assert.Contains("-Allowed @(200, 202)", apply);
        Assert.Contains("if ($Async) { $body.async = $true }", apply);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void README_tools_land_описує_порядок_шаблон_і_http()
    {
        var readme = File.ReadAllText(LandFile("README.md"));

        Assert.Contains("Land", readme);
        Assert.Contains("ECR_USER", readme);
        Assert.Contains("ECR_PASSWORD", readme);
        Assert.Contains("-DryRun", readme);
        Assert.Contains("http", readme);
        Assert.Contains("https", readme);
        Assert.True(
            readme.IndexOf("1. Довідники LAND_*: `Import-ContractDictionaries.ps1`", StringComparison.Ordinal) is var a and >= 0 &&
            readme.IndexOf("2. Шапка: `Apply-ContractHeader.ps1", StringComparison.Ordinal) is var b and >= 0 && a < b,
            "Довідники мають іти перед шапкою.");
    }
}
