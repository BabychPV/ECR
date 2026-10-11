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
        Assert.Matches(@"\$hasMigration = @\(\$migrateIds \| Where-Object", apply);

        Assert.Contains("Test-EcrTemplateUnsuitable $pub", apply);
        Assert.Contains("Test-EcrTemplateUnsuitable $cl", apply);
        Assert.Contains("Stop-EcrTemplateUnsuitable", apply);
        Assert.Contains("ECR-TMPL-4228", File.ReadAllText(LandFile("EcrApi.ps1")));
        // 202 + jobId обробляється, `async` без -Async не надсилається.
        Assert.Contains("-Allowed @(200, 202)", apply);
        Assert.Contains("if ($Async) { $body.async = $true }", apply);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("'3,10'", "3,10")]
    [InlineData("'3;10'", "3,10")]
    [InlineData("'3 10'", "3,10")]
    [InlineData("'3', '10'", "3,10")]
    [InlineData("3, 10", "3,10")]
    [InlineData("'7'", "7")]
    [InlineData("''", "")]
    public void Список_id_документів_розбирається_з_рядка_і_масиву_а_не_читається_як_310(string argument, string expected)
    {
        var script = ". '" + LandFile("EcrApi.ps1").Replace("'", "''") + "'; " +
                     "$r = @(ConvertTo-DocumentIdList @(" + argument + ")); ($r | ForEach-Object { [string]$_ }) -join ','";
        var output = RunPowerShell(script);

        Assert.Equal(expected, output);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Некоректний_id_документа_відхиляється()
    {
        var script = ". '" + LandFile("EcrApi.ps1").Replace("'", "''") + "'; " +
                     "try { ConvertTo-DocumentIdList 'abc' | Out-Null; 'no-throw' } catch { 'threw' }";

        Assert.Equal("threw", RunPowerShell(script));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повторна_міграція_документа_на_цільовій_версії_дає_0_змін_а_параметр_приймає_рядок()
    {
        var apply = File.ReadAllText(LandFile("Apply-ContractHeader.ps1"));
        var api = File.ReadAllText(LandFile("EcrApi.ps1"));

        // [int[]] прочитав би «3,10» з `powershell -File` як 310.
        Assert.Contains("[string[]]$MigrateDocumentId", apply);
        Assert.DoesNotContain("[int[]]$MigrateDocumentId", apply);
        Assert.Contains("ConvertTo-DocumentIdList $MigrateDocumentId", apply);
        // Відмова сервера «уже на версії» (422, messageKey migrateSameVersion) — не помилка ні на dry-run, ні на apply.
        Assert.Contains("migrateSameVersion", api);
        Assert.Equal(2, Regex.Count(apply, @"Test-EcrMigrateSameVersion \$(preview|apply)"));
        Assert.True(
            apply.IndexOf("Test-EcrMigrateSameVersion $preview", StringComparison.Ordinal) <
            apply.IndexOf("Assert-EcrOk $preview", StringComparison.Ordinal),
            "Перевірка «вже на версії» має бути до Assert-EcrOk dry-run.");
        Assert.Contains("0 змін", apply);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void README_tools_land_не_радить_кому_без_лапок_для_powershell_file()
    {
        var readme = File.ReadAllText(LandFile("README.md"));

        Assert.Contains("powershell -NoProfile -File .\\Apply-ContractHeader.ps1 -TemplateCode Land -MigrateDocumentId \"3,10\"", readme);
        Assert.Contains("310", readme);
        Assert.DoesNotMatch(@"powershell[^\r\n]*-File[^\r\n]*-MigrateDocumentId [0-9]+,[0-9]+", readme);
    }

    private static string RunPowerShell(string script)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"PowerShell впав: {stderr}");
        return stdout.Trim();
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
