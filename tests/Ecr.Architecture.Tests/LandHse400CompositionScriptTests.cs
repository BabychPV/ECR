using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10: <c>tools/land/Import-LandHse400Composition.ps1</c> (склад газу HSE400 зі старої системи у нормалізований
/// довідник LAND_HSE400_COMPOSITION) - чиста логіка плану на синтетичних рядках і запобіжники (локальний API, локальне
/// підтверджене джерело, лише SELECT … WITH (NOLOCK), мутації лише з -ConfirmStand). Значень прод-даних у git немає.
/// </summary>
public sealed class LandHse400CompositionScriptTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

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

        // -File, а не -Command: аргументи з `;` і лапками не мають розбиратися вдруге.
        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-hse400-{Guid.NewGuid():N}.ps1");
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
    public void План_нормалізує_суфікси_бере_V2_для_HMB_і_запасний_гексан_і_лічить_пропущене()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $rows = @(
              [pscustomobject]@{ Year = 2026; Month = 1; Columns = @{
                  'FG_Internal_Density20' = 1.5; 'FG_Internal_Methane_mass' = 80.25; 'FG_Internal_C6_1_mass' = 0.5
                  'LPG_HMB_Density20' = 2.5; 'LPG_HMB_EMercaptan_mass' = 0.1; 'LPG_HMB_EMercaptanV2_mass' = 0.7
                  'FG_Makat_Density_20' = 0.9; 'FG_Makat_FuelLowerHeating_0' = 33.5; 'FG_Export_Vddg' = $null
                  'FG_HMB_C6_C10_mass' = 0.3; 'FG_HMB_C6_1_mass' = 0.9; 'EFID' = 'guid'; 'ID' = 7; 'Stream_104_Density20' = 1 } },
              [pscustomobject]@{ Year = 2026; Month = 2; Columns = @{ 'FG_Internal_Density20' = 1.75 } },
              [pscustomobject]@{ Year = 2026; Month = 13; Columns = @{ 'FG_Internal_Density20' = 9 } }
            )
            $plan = ConvertTo-LandHse400CompositionPlan -Rows $rows
            function V($y, $m, $s, $i) { (@($plan.Items | Where-Object { $_.Year -eq $y -and $_.Month -eq $m -and $_.Stream -eq $s -and $_.Indicator -eq $i } | ForEach-Object { $_.Value }) -join ';') }
            'DENS_FGI=' + (V 2026 1 'FG_Internal' 'Density20')
            'DENS_FGI_FEB=' + (V 2026 2 'FG_Internal' 'Density20')
            'DENS_LPGHMB=' + (V 2026 1 'LPG_HMB' 'Density20')
            'DENS_MAKAT=' + (V 2026 1 'FG_Makat' 'Density20')
            'HEAT_MAKAT=' + (V 2026 1 'FG_Makat' 'FuelLowerHeating0')
            'EMERC_HMB=' + (V 2026 1 'LPG_HMB' 'EMercaptan_mass')
            'C6_FALLBACK=' + (V 2026 1 'FG_Internal' 'C6_C10_mass')
            'C6_OWN=' + (V 2026 1 'FG_HMB' 'C6_C10_mass')
            'EXPORT_NULL=' + (V 2026 1 'FG_Export' 'Vddg')
            'STREAM104=' + (@($plan.Items | Where-Object { $_.Stream -like 'Stream*' }).Count)
            'COUNT=' + @($plan.Items).Count
            'SKIPPED=' + $plan.Skipped
            'FALLBACKS=' + $plan.Fallbacks
            'OVERRIDES=' + $plan.Overrides
            'CODE=' + ((@($plan.Items | Where-Object { $_.Stream -eq 'LPG_HMB' -and $_.Indicator -eq 'Density20' }) | ForEach-Object { $_.EntryCode }) -join '')
            'EMPTY=' + @((ConvertTo-LandHse400CompositionPlan -Rows @()).Items).Count
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("DENS_FGI=1.5", output, StringComparison.Ordinal);
        Assert.Contains("DENS_FGI_FEB=1.75", output, StringComparison.Ordinal);
        Assert.Contains("DENS_LPGHMB=2.5", output, StringComparison.Ordinal);
        Assert.Contains("DENS_MAKAT=0.9", output, StringComparison.Ordinal);       // Density_20 -> Density20
        Assert.Contains("HEAT_MAKAT=33.5", output, StringComparison.Ordinal);      // FuelLowerHeating_0 -> FuelLowerHeating0
        Assert.Contains("EMERC_HMB=0.7", output, StringComparison.Ordinal);        // EMercaptan_mass <- EMercaptanV2_mass
        Assert.Contains("C6_FALLBACK=0.5", output, StringComparison.Ordinal);      // C6_C10_mass порожній -> C6_1_mass
        Assert.Contains("C6_OWN=0.3", output, StringComparison.Ordinal);           // власний C6_C10_mass не перекривається
        Assert.Matches(@"EXPORT_NULL=\s*(\r?\n|$)", output);
        Assert.Contains("STREAM104=0", output, StringComparison.Ordinal);          // Stream_* - не префікс HSE400
        Assert.Contains("SKIPPED=2", output, StringComparison.Ordinal);            // null + місяць 13
        Assert.Contains("FALLBACKS=1", output, StringComparison.Ordinal);
        Assert.Contains("OVERRIDES=1", output, StringComparison.Ordinal);
        Assert.Contains("CODE=H202601_LPG_HMB_Density20", output, StringComparison.Ordinal);
        Assert.Contains("EMPTY=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Потік_за_текстом_Land_поля_за_правилом_старої_системи_без_регістру_в_обидва_боки()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            function P($t) { $r = Get-LandHse400StreamPrefix -Text $t; if ($null -eq $r) { '-' } else { $r } }
            'A=' + (P 'Fuel gas (HMB) - Топливный газ (МТБ)')
            'B=' + (P 'LPG (Internal) - СУГ (Внутренний)')
            'C=' + (P 'fuel GAS (internal)')
            'D=' + (P 'Makat Gas - Макатский газ')
            'E=' + (P 'Diesel - Дизель')
            'F=' + (P '')
            'G=' + (P 'LPG (HMB)')
            $plan = ConvertTo-LandHse400StreamPlan -Texts @('LPG (HMB) - СУГ (МТБ)', 'lpg (hmb) - суг (мтб)', 'Diesel', '  ', 'Fuel gas (Export) - Топливный газ (Товарный)')
            'ITEMS=' + @($plan.Items).Count
            'DUPS=' + $plan.Duplicates
            'UNMATCHED=' + $plan.Unmatched
            'CODESTABLE=' + ((Get-LandHse400StreamEntryCode -Text 'ABC') -eq (Get-LandHse400StreamEntryCode -Text 'abc'))
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("A=FG_HMB", output, StringComparison.Ordinal);
        Assert.Contains("B=LPG_Internal", output, StringComparison.Ordinal);
        Assert.Contains("C=FG_Internal", output, StringComparison.Ordinal);
        Assert.Contains("D=FG_Makat", output, StringComparison.Ordinal);
        Assert.Contains("E=-", output, StringComparison.Ordinal);
        Assert.Contains("F=-", output, StringComparison.Ordinal);
        Assert.Contains("G=LPG_HMB", output, StringComparison.Ordinal);
        Assert.Contains("ITEMS=2", output, StringComparison.Ordinal);
        Assert.Contains("DUPS=1", output, StringComparison.Ordinal);
        Assert.Contains("UNMATCHED=1", output, StringComparison.Ordinal);
        Assert.Contains("CODESTABLE=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_відмовляє_до_будь_якого_читання_на_чужому_API_чужому_джерелі_і_без_підтверджень()
    {
        var import = LandFile("Import-LandHse400Composition.ps1");

        var (remoteExit, remote) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'https://ecr.example.com' -SourceDatabase CopyDb -ConfirmSource CopyDb -ConfirmStand 'ecr.example.com' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, remoteExit);
        Assert.Contains("localhost", remote, StringComparison.Ordinal);

        var (srvExit, srv) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceServer 'sql01.corp.example.com' -SourceDatabase CopyDb -ConfirmSource CopyDb -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, srvExit);
        Assert.Contains("sql01.corp.example.com", srv, StringComparison.Ordinal);

        var (noConfirmExit, noConfirm) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceDatabase CopyDb -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, noConfirmExit);
        Assert.Contains("-ConfirmSource", noConfirm, StringComparison.Ordinal);

        var (standExit, stand) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094' -SourceDatabase CopyDb -ConfirmSource CopyDb; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, standExit);
        Assert.Contains("-ConfirmStand", stand, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Текст_скрипта_читає_лише_SELECT_NOLOCK_і_не_має_значень_а_запобіжники_стоять_до_підключення()
    {
        var text = File.ReadAllText(LandFile("Import-LandHse400Composition.ps1"));

        Assert.Contains("[string]$ConfirmStand", text, StringComparison.Ordinal);
        Assert.Contains("Set-EcrStandConfirmation", text, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalBaseUrl", text, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalSourceDatabase", text, StringComparison.Ordinal);
        Assert.Contains("ApplicationIntent", text, StringComparison.Ordinal);

        var sourceGuard = text.IndexOf("Assert-LocalSourceDatabase -Server", StringComparison.Ordinal);
        var apiGuard = text.IndexOf("Assert-LocalBaseUrl $BaseUrl", StringComparison.Ordinal);
        var open = text.IndexOf("$conn.Open()", StringComparison.Ordinal);
        var login = text.IndexOf("Connect-Ecr -BaseUrl", StringComparison.Ordinal);
        Assert.True(apiGuard >= 0 && apiGuard < open, "Локальність API перевіряється до читання джерела.");
        Assert.True(sourceGuard >= 0 && sourceGuard < open, "Підтвердження джерела - до підключення.");
        Assert.True(open < login, "Спершу читання джерела, потім вхід у стенд.");

        // Усі запити до джерела - SELECT; усе, що читає дані (dbo.), - з NOLOCK; жодних DML.
        var commands = Regex.Matches(text, @"CommandText\s*=\s*""?'?(?<sql>[^\r\n]+)", RegexOptions.CultureInvariant);
        Assert.True(commands.Count >= 3, "Запити до джерела не знайдено.");
        foreach (Match command in commands)
        {
            var sql = command.Groups["sql"].Value;
            Assert.StartsWith("SELECT", sql.TrimStart('"', '\''), StringComparison.Ordinal);
            Assert.DoesNotMatch(@"(?i)\b(INSERT|UPDATE|DELETE|MERGE|DROP|ALTER|TRUNCATE|EXEC)\b", sql);
            if (sql.Contains("dbo.", StringComparison.Ordinal))
            {
                Assert.Contains("WITH (NOLOCK)", sql, StringComparison.Ordinal);
            }
        }

        // Значень складу в git немає: у файлах скрипта й бібліотеки жодного числового літерала з десятковою частиною
        // і жодного присвоєння значення показнику.
        foreach (var name in new[] { "Import-LandHse400Composition.ps1", "LandHse400Composition.Lib.ps1" })
        {
            Assert.DoesNotMatch(@"['""]\d+[.,]\d+['""]", File.ReadAllText(LandFile(name)));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void У_моделі_Land_і_документації_немає_значень_складу_лише_будова()
    {
        var dir = Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template");
        using var model = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "land-template.model.json")));
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .ToDictionary(r => r.GetProperty("code").GetString()!, r => r);

        // Склад - порожній: його значення вантажить скрипт із локальної копії прода.
        Assert.Empty(registries["LAND_HSE400_COMPOSITION"].GetProperty("entries").EnumerateArray());

        // Словник потоків - це тексти й префікси (код старої системи й випадні списки шаблону), а не значення складу.
        foreach (var entry in registries["LAND_HSE400_STREAM"].GetProperty("entries").EnumerateArray())
        {
            var fields = entry.GetProperty("fields");
            Assert.Equal(2, fields.EnumerateObject().Count());
            Assert.Matches("^(FG_(Makat|HMB|Export|Internal)|LPG_(HMB|Internal))$", fields.GetProperty("PREFIX").GetString()!);
        }
    }
}
