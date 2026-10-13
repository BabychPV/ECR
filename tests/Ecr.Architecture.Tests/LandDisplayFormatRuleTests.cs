using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-21 (Аудит, другий цикл на копії): <c>DisplayFormat</c> у шаблоні Land стояв лише у 595 із 2304 Decimal-колонок -
/// модель несе формат тільки там, де xlsm має числовий формат домінантного стилю даних (ширше за 80 % рядків;
/// порожні колонки й «General» формату не мають). Рішення людини 12.10: «Decimal 16 зберігаємо до 16 знаків,
/// показуємо за DisplayFormat колонки», тобто формат потрібен кожній Decimal-колонці.
/// Правило <c>Resolve-LandDisplayFormat</c> (tools/land/LandTemplateDiff.Lib.ps1; Build записує, Verify звіряє):
/// формат із моделі, інакше <c>0.</c> + min(Scale, 6) нулів. Тест виконує СПРАВЖНЮ функцію в PowerShell на
/// справжній моделі: без правила (або при втраті колонок) він червоний.
/// </summary>
public sealed class LandDisplayFormatRuleTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static string ModelFile => Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        . $args[0]
        $mode = $args[1]
        if ($mode -eq 'synthetic') {
            function Col($type, $scale, $fmt) {
                $o = [ordered]@{ code = 'X'; dataType = $type }
                if ($null -ne $scale) { $o.scale = $scale }
                if ($fmt) { $o.displayFormat = $fmt }
                return [pscustomobject]$o
            }
            Write-Host ('S16=' + (Resolve-LandDisplayFormat (Col 'Decimal' 16 $null)))
            Write-Host ('S2=' + (Resolve-LandDisplayFormat (Col 'Decimal' 2 $null)))
            Write-Host ('S6=' + (Resolve-LandDisplayFormat (Col 'Decimal' 6 $null)))
            Write-Host ('S0=' + (Resolve-LandDisplayFormat (Col 'Decimal' 0 $null)))
            Write-Host ('EXCEL=' + (Resolve-LandDisplayFormat (Col 'Decimal' 16 '0.00')))
            Write-Host ('EXCELPCT=' + (Resolve-LandDisplayFormat (Col 'Decimal' 16 '0.0000000%')))
            Write-Host ('NOSCALE=[' + (Resolve-LandDisplayFormat (Col 'Decimal' $null $null)) + ']')
            Write-Host ('STRING=[' + (Resolve-LandDisplayFormat (Col 'String' 16 $null)) + ']')
            Write-Host ('CALC=[' + (Resolve-LandDisplayFormat (Col 'Calculated' 16 $null)) + ']')
            Write-Host ('CALCEXPLICIT=' + (Resolve-LandDisplayFormat (Col 'Calculated' 16 '0.000')))
            Write-Host ('NULL=[' + (Resolve-LandDisplayFormat $null) + ']')
        }
        elseif ($mode -eq 'model') {
            $m = Get-Content -LiteralPath $args[2] -Raw -Encoding UTF8 | ConvertFrom-Json
            $dec = 0; $explicit = 0; $explicitKept = 0; $derived = 0; $missing = @(); $fmts = @{}
            foreach ($sh in @($m.sheets)) {
                foreach ($tb in @($sh.tables)) {
                    foreach ($c in @($tb.columns)) {
                        if ([string]$c.dataType -ne 'Decimal') { continue }
                        $dec++
                        $r = Resolve-LandDisplayFormat $c
                        if (-not $r) { $missing += "$($tb.code).$($c.code)"; continue }
                        if ($c.displayFormat) { $explicit++; if ($r -eq [string]$c.displayFormat) { $explicitKept++ } } else { $derived++ }
                        $fmts[$r] = 1 + [int]$fmts[$r]
                    }
                }
            }
            Write-Host ('DECIMAL=' + $dec)
            Write-Host ('EXPLICIT=' + $explicit)
            Write-Host ('EXPLICITKEPT=' + $explicitKept)
            Write-Host ('DERIVED=' + $derived)
            Write-Host ('MISSING=' + $missing.Count)
            foreach ($x in @($missing | Select-Object -First 10)) { Write-Host ('MISSINGCOL=' + $x) }
            Write-Host ('LONGEST=' + (($fmts.Keys | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum))
            Write-Host ('HAS000000=' + $fmts.ContainsKey('0.000000'))
        }
        """;

    private static string Run(params string[] extra)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-dfmt-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
                         LandFile("LandTemplateDiff.Lib.ps1"),
                     }.Concat(extra))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 180 с.");
            }

            Assert.True(process.ExitCode == 0, stdout + stderr);
            return stdout.Replace("\r\n", "\n");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Правило_виводить_нуль_цілих_із_Scale_до_шести_знаків_а_формат_Excel_не_чіпає()
    {
        var output = Run("synthetic");

        Assert.Contains("S16=0.000000", output, StringComparison.Ordinal);   // Scale 16 -> показ обмежено шістьма
        Assert.Contains("S2=0.00", output, StringComparison.Ordinal);
        Assert.Contains("S6=0.000000", output, StringComparison.Ordinal);
        Assert.Contains("S0=0\n", output, StringComparison.Ordinal);
        Assert.Contains("EXCEL=0.00\n", output, StringComparison.Ordinal);        // формат xlsm має пріоритет
        Assert.Contains("EXCELPCT=0.0000000%", output, StringComparison.Ordinal);
        Assert.Contains("NOSCALE=[]", output, StringComparison.Ordinal);   // без Scale формат не вигадується
        Assert.Contains("STRING=[]", output, StringComparison.Ordinal);    // не Decimal без явного формату - порожньо
        Assert.Contains("CALC=[]", output, StringComparison.Ordinal);
        Assert.Contains("CALCEXPLICIT=0.000", output, StringComparison.Ordinal);
        Assert.Contains("NULL=[]", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_Decimal_колонка_моделі_Land_отримує_формат_відображення()
    {
        var output = Run("model", ModelFile);

        // Було 595 із 2304: решта (порожні в xlsm й General) формату не мала. Винятків немає.
        Assert.Contains("MISSING=0\n", output, StringComparison.Ordinal);
        Assert.Matches(@"DECIMAL=(\d{4,})", output);

        // Формат Excel не втрачено і не перебито правилом.
        var explicitCount = Number(output, "EXPLICIT");
        Assert.True(explicitCount > 500, $"Колонок з форматом Excel лише {explicitCount}: перегенеруйте модель.");
        Assert.Equal(explicitCount, Number(output, "EXPLICITKEPT"));
        Assert.Equal(Number(output, "DECIMAL"), explicitCount + Number(output, "DERIVED"));

        // Формат вміщається в cfg.ColumnDef.DisplayFormat (nvarchar(50)).
        Assert.InRange(Number(output, "LONGEST"), 1, 50);
        Assert.Contains("HAS000000=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_записує_а_Verify_звіряє_формат_через_те_саме_правило()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var verify = File.ReadAllText(LandFile("Verify-LandTemplate.ps1"));

        // Build: і масова презентація колонок, і PUT нової колонки беруть формат з правила, а не з голого поля моделі.
        Assert.True(build.Split("Resolve-LandDisplayFormat").Length - 1 >= 2, "Build не викликає правило двічі.");
        Assert.DoesNotContain("$c.displayFormat", build, StringComparison.Ordinal);
        Assert.Contains("Resolve-LandDisplayFormat $mcol", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("$mcol.displayFormat", verify, StringComparison.Ordinal);
    }

    private static int Number(string output, string key)
    {
        var line = output.Split('\n').Select(l => l.Trim())
            .Single(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        return int.Parse(line[(key.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }
}
