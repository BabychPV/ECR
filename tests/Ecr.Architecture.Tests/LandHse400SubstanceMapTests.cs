using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10 (HSE400, склад ПО РЕЧОВИНІ): довідник <c>LAND_HSE400_SUBSTANCE</c> (код речовини -> показник
/// <c>{хімічна назва}_mass</c>) у моделі Land, фрагмент <c>CompositionMass</c> для формул методологій і скрипти
/// <c>Import-LandHse400Substance.ps1</c> / підстановка в <c>Build-LandTemplate.ps1 -Stage CompositionMass</c>.
/// Джерело правди - старий бекенд (<c>PollutionCodeService.GetMassName</c>, 15 кодів). Нових функцій рушія й міграцій
/// немає; значень прод-даних у git немає.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): у <c>hse400_bridges.py</c> змінити будь-який показник словника й
/// перегенерувати модель — червоніє <see cref="Словник_показників_збігається_з_15_кодами_старого_бекенду"/>; у
/// <c>Convert-LandHse400CompositionMassFormula</c> прибрати <c>(?![A-Za-z0-9_])</c> — червоніє
/// <see cref="Підстановка_замінює_лише_токен_CompositionMass_і_є_ідемпотентною"/>.
/// </remarks>
public sealed class LandHse400SubstanceMapTests
{
    // PollutionCodeService.BaseCodes (DllProject/Services/PollutionCodeService.cs), без прод-даних.
    private static readonly Dictionary<string, string> Legacy = new()
    {
        ["333"] = "H2S_mass",
        ["334"] = "CS2_mass",
        ["370"] = "COS_mass",
        ["415"] = "C1_C5_mass",
        ["416"] = "C6_C10_mass",
        ["602"] = "Benzene_mass",
        ["616"] = "pXylene_mass",
        ["621"] = "Toluene_mass",
        ["627"] = "EBenzene_mass",
        ["1702"] = "nBMercaptan_mass",
        ["1707"] = "DimethilSulfide_mass",
        ["1715"] = "MMercaptan_mass",
        ["1720"] = "nPMercaptan_mass",
        ["1728"] = "EMercaptan_mass",
        ["2754"] = "C12_C19_mass",
    };

    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));

    private static (int Exit, string Output) RunPowerShell(string script, params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-hse400s-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(args))
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
    public void Словник_показників_збігається_з_15_кодами_старого_бекенду()
    {
        using var model = Load();
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .ToDictionary(r => r.GetProperty("code").GetString()!, r => r);
        var substance = registries["LAND_HSE400_SUBSTANCE"];

        // Ключ - SUBSTANCE_CODE (а не код запису): REGFIND без первинного ключа шукав би за кодом запису.
        var key = Assert.Single(substance.GetProperty("keys").EnumerateArray());
        Assert.True(key.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(["SUBSTANCE_CODE"], key.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());

        var actual = substance.GetProperty("entries").EnumerateArray().ToDictionary(
            e => e.GetProperty("fields").GetProperty("SUBSTANCE_CODE").GetString()!,
            e => e.GetProperty("fields").GetProperty("INDICATOR").GetString()!);
        Assert.Equal(Legacy.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));

        // Код запису літера + число (EcrCode вимагає літери першою) і не збігається з кодом довідника речовин (E<код>).
        foreach (var entry in substance.GetProperty("entries").EnumerateArray())
        {
            var code = entry.GetProperty("code").GetString()!;
            Assert.Equal("C" + entry.GetProperty("fields").GetProperty("SUBSTANCE_CODE").GetString(), code);
        }

        // Показник - ім'я колонки складу в канонічному написанні (`..._mass`); V2 для HMB робить завантаження складу.
        foreach (var indicator in actual.Values)
        {
            Assert.Matches(@"^[A-Za-z][A-Za-z0-9]*(_[A-Za-z0-9]+)*_mass$", indicator);
            Assert.DoesNotContain("V2", indicator, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void CompositionMass_лишається_без_колонки_моста_а_причина_і_фрагменти_називають_довідники_моделі()
    {
        using var model = Load();
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .Select(r => r.GetProperty("code").GetString()!).ToHashSet();
        var bridges = model.RootElement.GetProperty("argumentBridges");

        var withReason = 0;
        foreach (var table in bridges.GetProperty("unresolved").EnumerateObject())
        {
            if (!table.Value.TryGetProperty("CompositionMass", out var reason))
            {
                continue;
            }

            withReason++;
            Assert.Contains("LAND_HSE400_SUBSTANCE", reason.GetString()!, StringComparison.Ordinal);
        }

        Assert.Equal(19, withReason);

        var hse = bridges.GetProperty("hse400");
        Assert.Contains(
            hse.GetProperty("registries").EnumerateArray(),
            r => r.GetString() == "LAND_HSE400_SUBSTANCE");

        var composition = hse.GetProperty("compositionMass");
        Assert.Contains("#N/A", composition.GetProperty("rule").GetString()!, StringComparison.Ordinal);
        foreach (var methodology in composition.GetProperty("methodologies").EnumerateObject())
        {
            var fragment = methodology.Value.GetProperty("fragment").GetString()!;
            foreach (Match m in Regex.Matches(fragment, @"REG(?:FIND|ONE)\('([^']+)'"))
            {
                Assert.Contains(m.Groups[1].Value, registries);
            }

            Assert.DoesNotContain("@CompositionMass", fragment, StringComparison.Ordinal);
            Assert.Contains("CURRENT_SUBSTANCE()", fragment, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_довідника_береться_з_моделі_і_відкидає_зіпсовані_записи()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $model = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $reg = @($model.registries) | Where-Object { $_.code -eq 'LAND_HSE400_SUBSTANCE' } | Select-Object -First 1
            $plan = ConvertTo-LandHse400SubstancePlan -Registry $reg
            'COUNT=' + @($plan.Items).Count
            'SKIPPED=' + $plan.Skipped
            $h2s = @($plan.Items | Where-Object { $_.Code -eq '333' })[0]
            'H2S=' + $h2s.Indicator + ';' + $h2s.EntryCode
            $bad = [pscustomobject]@{ entries = @(
                [pscustomobject]@{ fields = [pscustomobject]@{ SUBSTANCE_CODE = '333'; INDICATOR = 'H2S_mass' } },
                [pscustomobject]@{ fields = [pscustomobject]@{ SUBSTANCE_CODE = '333'; INDICATOR = 'Dup_mass' } },
                [pscustomobject]@{ fields = [pscustomobject]@{ SUBSTANCE_CODE = '05 01 03*'; INDICATOR = 'X_mass' } },
                [pscustomobject]@{ fields = [pscustomobject]@{ SUBSTANCE_CODE = '999'; INDICATOR = 'no suffix' } }) }
            $p2 = ConvertTo-LandHse400SubstancePlan -Registry $bad
            'BAD_COUNT=' + @($p2.Items).Count
            'BAD_SKIPPED=' + $p2.Skipped
            """;

        var (exit, output) = RunPowerShell(
            script, LandFile("LandHse400Composition.Lib.ps1"), Path.Combine(Dir, "land-template.model.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=15", output, StringComparison.Ordinal);
        Assert.Contains("SKIPPED=0", output, StringComparison.Ordinal);
        Assert.Contains("H2S=H2S_mass;C333", output, StringComparison.Ordinal);
        Assert.Contains("BAD_COUNT=1", output, StringComparison.Ordinal);
        Assert.Contains("BAD_SKIPPED=3", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підстановка_замінює_лише_токен_CompositionMass_і_є_ідемпотентною()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $frag = "REGFIELD(REGFIND('C', @Land_TypeFuelOrStream), 'VALUE')"
            $r = Convert-LandHse400CompositionMassFormula -Expression 'if (@Land_Measure_OperatingTime = 0, 0, @CompositionMass * @Land_Measure_OperatingTime)' `
                -ArgumentsCsv "@CompositionMass;`n@Land_Measure_OperatingTime;CST.k1_;" -Fragment $frag -StreamArguments @('Land_TypeFuelOrStream')
            'CHANGED=' + $r.Changed
            'EXPR=' + $r.Expression
            'ARGS=' + $r.ArgumentsCsv
            $again = Convert-LandHse400CompositionMassFormula -Expression $r.Expression -ArgumentsCsv $r.ArgumentsCsv -Fragment $frag -StreamArguments @('Land_TypeFuelOrStream')
            'AGAIN=' + $again.Changed
            'AGAIN_SAME=' + (($again.Expression -ceq $r.Expression) -and ($again.ArgumentsCsv -ceq $r.ArgumentsCsv))
            $longer = Convert-LandHse400CompositionMassFormula -Expression '@CompositionMassTotal + 1' -ArgumentsCsv '@CompositionMassTotal;' -Fragment $frag
            'LONGER=' + $longer.Changed
            $nullArgs = Convert-LandHse400CompositionMassFormula -Expression '@CompositionMass' -ArgumentsCsv $null -Fragment $frag
            'NULLARGS=' + $nullArgs.Changed + ';' + ($null -eq $nullArgs.ArgumentsCsv)
            $dup = Convert-LandHse400CompositionMassFormula -Expression '@CompositionMass' -ArgumentsCsv '@CompositionMass;@Land_TypeFuelOrStream;' -Fragment $frag -StreamArguments @('Land_TypeFuelOrStream')
            'DUP=' + $dup.ArgumentsCsv
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("CHANGED=True", output, StringComparison.Ordinal);
        Assert.Contains(
            "EXPR=if (@Land_Measure_OperatingTime = 0, 0, REGFIELD(REGFIND('C', @Land_TypeFuelOrStream), 'VALUE') * @Land_Measure_OperatingTime)",
            output,
            StringComparison.Ordinal);
        Assert.Contains("ARGS=@Land_Measure_OperatingTime;CST.k1_;@Land_TypeFuelOrStream;", output, StringComparison.Ordinal);
        Assert.Contains("AGAIN=False", output, StringComparison.Ordinal);
        Assert.Contains("AGAIN_SAME=True", output, StringComparison.Ordinal);
        Assert.Contains("LONGER=False", output, StringComparison.Ordinal);
        Assert.Contains("NULLARGS=True;True", output, StringComparison.Ordinal);
        Assert.Contains("DUP=@Land_TypeFuelOrStream;", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_довідника_відмовляє_на_чужому_API_і_без_підтвердження_стенда_і_не_читає_прод()
    {
        var import = LandFile("Import-LandHse400Substance.ps1");

        var (remoteExit, remote) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'https://ecr.example.com' -ConfirmStand 'ecr.example.com' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, remoteExit);
        Assert.Contains("localhost", remote, StringComparison.Ordinal);

        var (standExit, stand) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094'; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, standExit);
        Assert.Contains("-ConfirmStand", stand, StringComparison.Ordinal);

        // Відповідність береться з моделі: до прод-БД скрипт не підключається й значень не має.
        var text = File.ReadAllText(import);
        Assert.DoesNotContain("SqlConnection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"['""]\d+[.,]\d+['""]", text);
        Assert.True(
            text.IndexOf("Assert-LocalBaseUrl $BaseUrl", StringComparison.Ordinal)
            < text.IndexOf("Connect-Ecr -BaseUrl", StringComparison.Ordinal),
            "Локальність API перевіряється до входу в стенд.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_CompositionMass_лише_явний_і_не_чіпає_опубліковані_версії()
    {
        var text = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("'LandParity', 'CompositionMass', 'All')", text, StringComparison.Ordinal);
        Assert.Contains("if ($Stage -contains 'CompositionMass') { Stage-CompositionMass }", text, StringComparison.Ordinal);

        var start = text.IndexOf("function Stage-CompositionMass", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = text[start..text.IndexOf("function Stage-Documents", start, StringComparison.Ordinal)];
        Assert.Contains("-eq 'Published'", body, StringComparison.Ordinal);
        Assert.Contains("Convert-LandHse400CompositionMassFormula", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/publish", body, StringComparison.Ordinal);
    }
}
