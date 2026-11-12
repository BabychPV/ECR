using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Land 1.0.16.0 (людина 10.11, координатор 11.11): перелік обов'язкових входів методологій Land
/// (<c>tools/land/LandRequiredInputs.Lib.ps1</c>) і правила етапу <c>Build-LandTemplate.ps1 -Stage RequiredInputs</c>:
/// кожна названа колонка є в моделі шаблону; чужу чернетку («test») етап не чіпає; номер нової чернетки не залежить від
/// назви останньої версії; повтор — 0 змін, зміна рівня — повторна реєстрація.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у бібліотеці змінити код колонки (<c>Land_RotationRef</c> → <c>Land_Rotation</c>);
/// прибрати з <c>ECW_C05_03</c> одну з чотирьох колонок людини; у <c>Get-LandRequiredInputTarget</c> прибрати фільтр
/// <c>^[Vv]\d+$</c>; у <c>Get-LandRequiredInputDiff</c> прибрати порівняння рівня — відповідні тести червоніють.
/// </remarks>
public sealed class LandRequiredInputsTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_обовязкова_колонка_є_в_моделі_шаблону_і_не_є_місячною()
    {
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            foreach ($m in Get-LandRequiredInputMethodologies) { foreach ($r in Get-LandRequiredInputs -Methodology $m) { Write-Host ('REQ=' + $m + '|' + $r.Path) } }
            """;

        var required = Run(harness, LandFile("LandRequiredInputs.Lib.ps1"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("REQ=", StringComparison.Ordinal))
            .Select(l => l[4..].Split('|'))
            .Select(p => (Methodology: p[0], Path: p[1]))
            .ToList();

        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

        var columns = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray()
                .Select(c => $"{t.GetProperty("code").GetString()}/{c.GetProperty("code").GetString()}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(required.Count > 100, $"перелік порожній або обрізаний: {required.Count}");
        Assert.Empty(required.Where(r => !columns.Contains(r.Path)).Select(r => $"{r.Methodology}: {r.Path}"));

        // місячний вхід вимогою бути не може: gate перевіряє рядок, а не місяць (відома прогалина, RI2)
        Assert.Empty(required.Where(r => System.Text.RegularExpressions.Regex.IsMatch(r.Path, "_M(0[1-9]|1[0-2])$")).Select(r => r.Path));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Чотири_колонки_людини_зареєстровано_для_методологій_C05_01_05()
    {
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            foreach ($m in 'ECW_C05_01', 'ECW_C05_02', 'ECW_C05_03', 'ECW_C05_04', 'ECW_C05_05') {
                $p = @(Get-LandRequiredInputs -Methodology $m | ForEach-Object { $_.Path })
                $miss = @('T3_00/Land_TypeEquipment', 'T3_00/Land_RotationRef', 'T3_00/Land_DescriptionEquipment', 'T3_00/Land_TypeFuel' | Where-Object { $p -cnotcontains $_ })
                Write-Host ($m + '=' + ($miss -join ',') + ';dup=' + ($p.Count - @($p | Sort-Object -Unique).Count))
            }
            Write-Host ('NONE=' + @(Get-LandRequiredInputs -Methodology 'ECW_NO_SUCH').Count)
            """;

        var output = Run(harness, LandFile("LandRequiredInputs.Lib.ps1"));

        foreach (var code in new[] { "ECW_C05_01", "ECW_C05_02", "ECW_C05_03", "ECW_C05_04", "ECW_C05_05" })
        {
            Assert.Contains($"{code}=;dup=0\n", output, StringComparison.Ordinal);
        }

        Assert.Contains("NONE=0\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Чужу_чернетку_не_чіпаємо_а_номер_нової_не_залежить_від_назви_останньої_версії()
    {
        // як на живій базі: ECW_C05_01 - V1 опублікована і чернетка «test»; ECW_C12_01 - V1, V2 і опублікована «test»
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            function V($id, $n, $s) { [pscustomobject]@{ id = $id; versionNumber = $n; status = $s } }
            $a = Get-LandRequiredInputTarget -Versions @((V 2 'V1' 'Published'), (V 58 'test' 'Draft'))
            Write-Host ('A=' + [string]$a.Draft.id + '|' + $a.Base.id + '|' + (@($a.Foreign | ForEach-Object { $_.id }) -join ',') + '|' + $a.Next)
            $b = Get-LandRequiredInputTarget -Versions @((V 40 'V1' 'Archived'), (V 51 'V2' 'Published'), (V 59 'test' 'Published'))
            Write-Host ('B=' + [string]$b.Draft.id + '|' + $b.Base.id + '|' + @($b.Foreign).Count + '|' + $b.Next)
            $c = Get-LandRequiredInputTarget -Versions @((V 15 'V1' 'Archived'), (V 54 'V3' 'Published'), (V 70 'V4' 'Draft'), (V 66 'v9' 'Draft'))
            Write-Host ('C=' + [string]$c.Draft.id + '|' + $c.Base.id + '|' + @($c.Foreign).Count + '|' + $c.Next)
            $d = Get-LandRequiredInputTarget -Versions @()
            Write-Host ('D=' + [string]$d.Draft.id + '|' + [string]$d.Base.id + '|' + $d.Next)
            """;

        var output = Run(harness, LandFile("LandRequiredInputs.Lib.ps1"));

        Assert.Contains("A=|2|58|V2\n", output, StringComparison.Ordinal);
        Assert.Contains("B=|59|0|V3\n", output, StringComparison.Ordinal);
        Assert.Contains("C=70|54|0|V10\n", output, StringComparison.Ordinal);
        Assert.Contains("D=||V1\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повтор_дає_нуль_змін_а_зміна_рівня_реєструє_заново()
    {
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            $w = @([pscustomobject]@{ Path = 'T3_00/Land_TypeFuel' }, [pscustomobject]@{ Path = 'T3_00/Land_RotationRef' })
            $none = @()
            $warn = @([pscustomobject]@{ Path = 'T3_00/Land_TypeFuel'; Severity = 'Warn' }, [pscustomobject]@{ Path = 'T3_00/Land_RotationRef'; Severity = 'Warn' },
                      [pscustomobject]@{ Path = $null; Severity = 'Warn' })
            $half = @([pscustomobject]@{ Path = 'T3_00/Land_TypeFuel'; Severity = 'Block' })
            Write-Host ('NEW=' + (@(Get-LandRequiredInputDiff -Wanted $w -Current $none -Severity Warn | ForEach-Object { $_.Path }) -join ','))
            Write-Host ('SAME=' + @(Get-LandRequiredInputDiff -Wanted $w -Current $warn -Severity Warn).Count)
            Write-Host ('LEVEL=' + @(Get-LandRequiredInputDiff -Wanted $w -Current $warn -Severity Block).Count)
            Write-Host ('HALF=' + (@(Get-LandRequiredInputDiff -Wanted $w -Current $half -Severity Block | ForEach-Object { $_.Path }) -join ','))
            """;

        var output = Run(harness, LandFile("LandRequiredInputs.Lib.ps1"));

        Assert.Contains("NEW=T3_00/Land_TypeFuel,T3_00/Land_RotationRef\n", output, StringComparison.Ordinal);
        Assert.Contains("SAME=0\n", output, StringComparison.Ordinal);
        Assert.Contains("LEVEL=2\n", output, StringComparison.Ordinal);
        Assert.Contains("HALF=T3_00/Land_RotationRef\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_лише_явний_типово_Warn_і_не_публікує()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("[ValidateSet('Warn', 'Block')][string]$RequiredInputSeverity = 'Warn'", build, StringComparison.Ordinal);
        Assert.Contains("if ($Stage -contains 'RequiredInputs') { Stage-RequiredInputs }", build, StringComparison.Ordinal);

        var start = build.IndexOf("function Stage-RequiredInputs {", StringComparison.Ordinal);
        var end = build.IndexOf("\nfunction ", start + 1, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "Stage-RequiredInputs є в збірці");
        var stage = build[start..end];

        // публікація методологій - крок вікна, не етапу; нова чернетка - лише за явним прапором
        Assert.DoesNotContain("/publish", stage, StringComparison.Ordinal);
        Assert.Contains("if (-not $RequiredInputsCreateDrafts) {", stage, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Методологію_на_утриманні_етап_пропускає_а_перелік_її_зберігає()
    {
        // ECW_C11_01: AN-165 приносить нову версію методології, чернетка PE з нею зіткнулася б (AN 11.11)
        const string harness = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            . $args[0]
            $h = Get-LandRequiredInputHeld
            Write-Host ('HELD=' + (@($h.Keys) -join ','))
            Write-Host ('KNOWN=' + @($h.Keys | Where-Object { (Get-LandRequiredInputMethodologies) -ccontains $_ }).Count)
            Write-Host ('LIST=' + (@(Get-LandRequiredInputs -Methodology 'ECW_C11_01').Count -gt 0))
            """;

        var output = Run(harness, LandFile("LandRequiredInputs.Lib.ps1"));

        Assert.Contains("HELD=ECW_C11_01\n", output, StringComparison.Ordinal);
        Assert.Contains("KNOWN=1\n", output, StringComparison.Ordinal);
        Assert.Contains("LIST=True\n", output, StringComparison.Ordinal);

        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        Assert.Contains(
            "if (-not $RequiredInputsIncludeHeld -and $riHeld.Contains($riCode)) {", build, StringComparison.Ordinal);
    }

    private static string Run(string harness, params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-reqinputs-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }.Concat(arguments))
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
            return stdout.Replace("\r", string.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
