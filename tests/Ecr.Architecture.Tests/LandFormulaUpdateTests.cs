using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-YE-1 (RC «Є», верифікація 13.10): Build-LandTemplate.ps1 на клоні лише ВСТАВЛЯВ відсутні формули колонок, а вираз
/// наявної колонки лишався старим (T5_08/T5_09 Winter <c>&lt; 4 OR &gt; 9</c> замість <c>&lt; 3 OR &gt; 8</c>);
/// Verify-LandTemplate.ps1 вираз не порівнював взагалі. Тести виконують справжні функції
/// <c>tools/land/LandTemplateDiff.Lib.ps1</c> у PowerShell і перевіряють текстові інваріанти обох скриптів.
/// </summary>
public sealed class LandFormulaUpdateTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        . $args[0]
        $mode = $args[1]
        if ($mode -eq 'plan') {
            $wanted = @(
                [pscustomobject]@{ Key = '1/10'; Expression = '[Month] < 3 OR [Month] > 8'; Table = 1; Column = 10 },
                [pscustomobject]@{ Key = '1/11'; Expression = 'A  +   B'; Table = 1; Column = 11 },
                [pscustomobject]@{ Key = '1/12'; Expression = 'NEWF'; Table = 1; Column = 12 },
                [pscustomobject]@{ Key = '1/13'; Expression = 'C'; Table = 1; Column = 13 }
            )
            $existing = @{
                '1/10' = @{ Id = 100; Dialect = 0; Expression = '[Month] < 4 OR [Month] > 9' }
                '1/11' = @{ Id = 101; Dialect = 0; Expression = 'A + B' }
                '1/13' = @{ Id = 103; Dialect = 1; Expression = 'C' }
            }
            $p = Get-LandFormulaPlan $wanted $existing
            Write-Host ('INSERT=' + (@($p.Insert | ForEach-Object { $_.Key }) -join ','))
            Write-Host ('UPDATE=' + (@($p.Update | ForEach-Object { '{0}:{1}' -f $_.Id, $_.Expression }) -join ';'))
            Write-Host ('SAME=' + $p.Same)
            # ідемпотентність: після застосування плану повторний план порожній
            foreach ($u in @($p.Update)) { $k = $u.Key; $existing[$k] = @{ Id = $u.Id; Dialect = 0; Expression = $u.Expression } }
            foreach ($i in @($p.Insert)) { $existing[$i.Key] = @{ Id = 900; Dialect = 0; Expression = $i.Expression } }
            $p2 = Get-LandFormulaPlan $wanted $existing
            Write-Host ('REPLAN=' + @($p2.Insert).Count + '/' + @($p2.Update).Count + '/' + $p2.Same)
        }
        elseif ($mode -eq 'verify') {
            $mdl = '{"sheets":[{"code":"S5","tables":[{"code":"T5_08","columns":[
                {"code":"Winter","dataType":"Formula","expression":"[Month] < 3 OR [Month] > 8"},
                {"code":"Other","dataType":"Calculated","expression":"X + Y"},
                {"code":"Gone","dataType":"Calculated","expression":"Q"},
                {"code":"Plain","dataType":"Decimal"}]}]}]}' | ConvertFrom-Json
            $bad = '{"sheets":[{"code":"S5","tables":[{"code":"T5_08","columns":[
                {"code":"Winter","isReadOnly":true,"formulaExpression":"[Month] < 4 OR [Month] > 9","formulaDialect":"Template"},
                {"code":"Other","isReadOnly":false,"formulaExpression":"X  +  Y","formulaDialect":"Template"},
                {"code":"Gone","isReadOnly":true,"formulaExpression":null},
                {"code":"Plain","isReadOnly":false}]}]}]}' | ConvertFrom-Json
            $good = '{"sheets":[{"code":"S5","tables":[{"code":"T5_08","columns":[
                {"code":"Winter","isReadOnly":true,"formulaExpression":"[Month] < 3 OR [Month] > 8","formulaDialect":"Template"},
                {"code":"Other","isReadOnly":true,"formulaExpression":"X + Y","formulaDialect":"Template"},
                {"code":"Gone","isReadOnly":true,"formulaExpression":"Q","formulaDialect":0},
                {"code":"Plain","isReadOnly":false}]}]}]}' | ConvertFrom-Json
            foreach ($l in @(Get-LandFormulaMismatches $bad $mdl)) { Write-Host ('BAD=' + $l) }
            Write-Host ('GOODCOUNT=' + @(Get-LandFormulaMismatches $good $mdl).Count)
        }
        """;

    private static string Run(string mode)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-formulas-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
                         LandFile("LandTemplateDiff.Lib.ps1"), mode,
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

            Assert.True(process.ExitCode == 0, stdout + stderr);
            return stdout;
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_формул_оновлює_старий_вираз_клону_вставляє_нові_і_ігнорує_пробіли()
    {
        var output = Run("plan");

        Assert.Contains("INSERT=1/12", output, StringComparison.Ordinal);
        // 1/10: вираз відрізняється; 1/13: діалект не Template; 1/11 відрізняється лише пробілами - не чіпається.
        Assert.Contains("UPDATE=100:[Month] < 3 OR [Month] > 8;103:C", output, StringComparison.Ordinal);
        Assert.Contains("SAME=1", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_формул_ідемпотентний_повторний_прогін_дає_нуль_змін()
    {
        var output = Run("plan");

        Assert.Contains("REPLAN=0/0/4", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_ловить_розбіжність_виразу_відсутню_формулу_і_readOnly_без_значень()
    {
        var output = Run("verify");

        // Кирилиця консольного виводу залежить від кодової сторінки: звіряємо адресу й колонку, не текст причини.
        var bad = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("BAD=", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, bad.Count);
        Assert.Single(bad, l => l.StartsWith("BAD=S5/T5_08: ", StringComparison.Ordinal) && l.EndsWith(": Winter", StringComparison.Ordinal));
        Assert.Single(bad, l => l.EndsWith(": Gone", StringComparison.Ordinal));
        Assert.Single(bad, l => l.EndsWith(": Other", StringComparison.Ordinal));
        Assert.Contains("GOODCOUNT=0", output, StringComparison.Ordinal);
        // значення виразів у перелік не потрапляють
        Assert.DoesNotContain("OR [Month]", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_порівнює_і_оновлює_формули_порціями_а_не_лише_вставляє()
    {
        var text = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("LandTemplateDiff.Lib.ps1", text, StringComparison.Ordinal);
        Assert.Contains("Get-LandFormulaPlan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("if ($haveF.ContainsKey(\"$tid/$cid\")) { continue }", text, StringComparison.Ordinal);
        Assert.Contains("UPDATE f SET Expression = p.Expression", text, StringComparison.Ordinal);
        Assert.Contains("$lo += 200", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_скрипт_використовує_порівняння_формул()
    {
        var text = File.ReadAllText(LandFile("Verify-LandTemplate.ps1"));

        Assert.Contains("LandTemplateDiff.Lib.ps1", text, StringComparison.Ordinal);
        Assert.Contains("Get-LandFormulaMismatches", text, StringComparison.Ordinal);
    }
}
