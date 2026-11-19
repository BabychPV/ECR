using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Етап <c>LegacyPublish</c> (Build-LandTemplate.ps1): золотий знімок виходів ПО РЕЧОВИНАХ. Чиста частина —
/// <c>tools/land/LandLegacyPublish.Lib.ps1</c>; СИНТЕТИЧНІ відповіді (вигадані коди виходів і числа), без мережі й БД.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>New-LandSnapshotExpected</c> замінити ключ <c>'{0}@{1}'</c> на <c>[string]$a.Output</c> —
/// червоніють <c>Поречовинний_вихід_дає_ключ_код_речовина_а_не_одне_число_на_код</c> і
/// <c>Текстова_відповідь_теж_несе_речовину</c>; у <c>Get-LandGoldenActuals</c> прибрати пропуск порожнього
/// <c>actual</c> — червоніє <c>Вихід_якого_не_було_у_знімок_не_потрапляє</c>.
/// </remarks>
public sealed class LandLegacyPublishLibTests
{
    private static string Lib => Path.Combine(SourceTree.Root, "tools", "land", "LandLegacyPublish.Lib.ps1");

    private static (int Exit, string Output) RunPowerShell(string script)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-lpub-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
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

    private static string Script(string body)
        => "$ErrorActionPreference = 'Stop'\n. '" + Lib.Replace("'", "''", StringComparison.Ordinal) + "'\n" + body;

    private const string Structured = """
        $json = '{"errorCode":"ECR-CALC-0422","goldenSet":[{"code":"SNAP_1","isGreen":false,"mismatches":[
          {"outputCode":"OUT_TONS","substanceEntryId":11,"actual":0.5,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_TONS","substanceEntryId":12,"actual":2.25,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_GSEC","substanceEntryId":null,"actual":7,"expected":-987654321,"tolerance":1e-9},
          {"outputCode":"OUT_BRANCH","substanceEntryId":null,"actual":null,"expected":-987654321,"tolerance":1e-9}]}]}' | ConvertFrom-Json
        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Поречовинний_вихід_дає_ключ_код_речовини_а_не_одне_число_на_код()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('OUT_TONS', 'OUT_GSEC', 'OUT_BRANCH')
            'COUNT=' + $e.Count
            'K=' + (($e.Keys | ForEach-Object { $_ + ':' + $e[$_] }) -join '|')
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=3", output, StringComparison.Ordinal);
        Assert.Contains("K=OUT_TONS@11:0.5|OUT_TONS@12:2.25|OUT_GSEC:7", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вихід_якого_не_було_у_знімок_не_потрапляє()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            'N=' + $a.Count
            'HAS_BRANCH=' + (@($a | Where-Object { $_.Output -eq 'OUT_BRANCH' }).Count)
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("N=3", output, StringComparison.Ordinal);
        Assert.Contains("HAS_BRANCH=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Текстова_відповідь_теж_несе_речовину()
    {
        var script = Script("""
            $text = "SNAP_1: OUT_TONS (речовина 11): очікували -987654321 ± 1e-9, отримали 0,5; SNAP_1: OUT_TONS (речовина 12): очікували -987654321 ± 1e-9, отримали 2.25; SNAP_1: OUT_GSEC (без речовини): очікували -987654321 ± 1e-9, отримали 7; SNAP_1: OUT_BRANCH (без речовини): очікували -987654321 ± 1e-9, отримали виходу не було"
            $a = @(Get-LandGoldenActuals -Text $text)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('OUT_TONS', 'OUT_GSEC', 'OUT_BRANCH')
            'K=' + (($e.Keys | ForEach-Object { $_ + ':' + $e[$_] }) -join '|')
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("K=OUT_TONS@11:0.5|OUT_TONS@12:2.25|OUT_GSEC:7", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Неоголошений_вихід_і_порожня_відповідь_дають_порожнє_очікування()
    {
        var script = Script(Structured + """

            $a = @(Get-LandGoldenActuals -Json $json)
            $e = New-LandSnapshotExpected -Actuals $a -Outputs @('ІНШИЙ')
            'COUNT=' + $e.Count
            $none = @(Get-LandGoldenActuals -Json $null -Text '')
            'NONE=' + $none.Count
            $e2 = New-LandSnapshotExpected -Actuals $none -Outputs @('OUT_TONS')
            'COUNT2=' + $e2.Count
            """);

        var (exit, output) = RunPowerShell(script);

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=0", output, StringComparison.Ordinal);
        Assert.Contains("NONE=0", output, StringComparison.Ordinal);
        Assert.Contains("COUNT2=0", output, StringComparison.Ordinal);
    }
}
