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

    /// <summary>
    /// K7-діагноз (C11_08, T9_08/T9_10): легасі читає HMB-<c>MercaptanSulphurContent</c> з колонки
    /// <c>*_TotalSulphurContentWithoutH2S</c> (Q_Air_HSE400.cs:127/:217), у довіднику показника не було (-> #ARG у tons_330).
    /// Мутація: прибрати блок «HMB: MercaptanSulphurContent» у ConvertTo-LandHse400CompositionPlan - тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void HMB_MercaptanSulphurContent_береться_з_TotalSulphurContentWithoutH2S_і_лише_для_HMB()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $rows = @(
              [pscustomobject]@{ Year = 2026; Month = 1; Columns = @{
                  'FG_HMB_TotalSulphurContentWithoutH2S' = 0.42; 'LPG_HMB_TotalSulphurContentWithoutH2S' = 0.17
                  'FG_Internal_TotalSulphurContentWithoutH2S' = 0.99; 'FG_Internal_MercaptanSulphurContent' = 0.05
                  'FG_Makat_TotalSulphurContentWithoutH2S' = 0.77 } },
              [pscustomobject]@{ Year = 2026; Month = 2; Columns = @{ 'FG_HMB_Density20' = 0.8 } }
            )
            $plan = ConvertTo-LandHse400CompositionPlan -Rows $rows
            function V($m, $s, $i) { (@($plan.Items | Where-Object { $_.Month -eq $m -and $_.Stream -eq $s -and $_.Indicator -eq $i } | ForEach-Object { $_.Value }) -join ';') }
            'FGHMB=' + (V 1 'FG_HMB' 'MercaptanSulphurContent')
            'LPGHMB=' + (V 1 'LPG_HMB' 'MercaptanSulphurContent')
            'FGHMB_KEEPS=' + (V 1 'FG_HMB' 'TotalSulphurContentWithoutH2S')
            'FGINT_OWN=' + (V 1 'FG_Internal' 'MercaptanSulphurContent')
            'MAKAT=' + (@($plan.Items | Where-Object { $_.Stream -eq 'FG_Makat' -and $_.Indicator -eq 'MercaptanSulphurContent' }).Count)
            'FEB=' + (@($plan.Items | Where-Object { $_.Month -eq 2 -and $_.Indicator -eq 'MercaptanSulphurContent' }).Count)
            'ALIASED=' + (@($plan.Items | Where-Object { $_.Indicator -eq 'MercaptanSulphurContent' -and $_.Aliased }).Count)
            'MERC_ALIASES=' + $plan.MercaptanAliases
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("FGHMB=0.42", output, StringComparison.Ordinal);
        Assert.Contains("LPGHMB=0.17", output, StringComparison.Ordinal);
        Assert.Contains("FGHMB_KEEPS=0.42", output, StringComparison.Ordinal);
        Assert.Contains("FGINT_OWN=0.05", output, StringComparison.Ordinal);   // не-HMB: власний показник, без псевдоніма
        Assert.Contains("MAKAT=0", output, StringComparison.Ordinal);
        Assert.Contains("FEB=0", output, StringComparison.Ordinal);            // немає джерела - немає запису
        Assert.Contains("ALIASED=0", output, StringComparison.Ordinal);        // не C*_mass-псевдонім: -RewriteAliased не зачіпає
        Assert.Contains("MERC_ALIASES=2", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// C12_03 (паритет 415/416/2754): стара система читає `FG_Internal_C1_C5` у властивість `FG_Internal_C1_C5_mass`
    /// (Q_Air_HSE400.Get_DataFromHSE400). Без псевдоніма довідник мав `C1_C5`/`C12_C19` (415/2754 без складу) і
    /// `C6_C10_mass` = запасна C6_1_mass (416 хибний, коли справжній C6_C10 непорожній).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонки_C1_C5_C6_C10_C12_C19_без_mass_стають_показником_з_mass_до_запасної_гексану()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $rows = @(
              [pscustomobject]@{ Year = 2026; Month = 1; Columns = @{
                  'FG_Internal_C1_C5' = 0.11; 'FG_Internal_C6_C10' = 0.22; 'FG_Internal_C12_C19' = 0.33; 'FG_Internal_C6_1_mass' = 0.44
                  'FG_Export_C1_C5' = 0.55; 'FG_Export_C6_1_mass' = 0.66
                  'LPG_HMB_C1_C5_mass' = 0.77
                  'FG_Makat_C6_1_mass' = 0.88 } },
              [pscustomobject]@{ Year = 2026; Month = 2; Columns = @{ 'FG_Internal_C6_C10' = $null; 'FG_Internal_C6_1_mass' = 0.5 } },
              [pscustomobject]@{ Year = 2026; Month = 3; Columns = @{ 'FG_Internal_C1_C5' = 1.5; 'FG_Internal_C1_C5_mass' = 2.5 } }
            )
            $plan = ConvertTo-LandHse400CompositionPlan -Rows $rows
            function V($m, $s, $i) { (@($plan.Items | Where-Object { $_.Month -eq $m -and $_.Stream -eq $s -and $_.Indicator -eq $i } | ForEach-Object { $_.Value }) -join ';') }
            function A($m, $s, $i) { (@($plan.Items | Where-Object { $_.Month -eq $m -and $_.Stream -eq $s -and $_.Indicator -eq $i } | ForEach-Object { $_.Aliased }) -join ';') }
            'C1C5=' + (V 1 'FG_Internal' 'C1_C5_mass')
            'C6C10_OWN=' + (V 1 'FG_Internal' 'C6_C10_mass')
            'C12C19=' + (V 1 'FG_Internal' 'C12_C19_mass')
            'C1C5_RAW=' + @($plan.Items | Where-Object { $_.Indicator -eq 'C1_C5' -or $_.Indicator -eq 'C6_C10' -or $_.Indicator -eq 'C12_C19' }).Count
            'EXPORT_C1C5=' + (V 1 'FG_Export' 'C1_C5_mass')
            'EXPORT_C6_FALLBACK=' + (V 1 'FG_Export' 'C6_C10_mass')
            'ALIASED=' + (A 1 'FG_Internal' 'C6_C10_mass')
            'FALLBACK_NOT_ALIASED=' + (A 1 'FG_Export' 'C6_C10_mass')
            'HMB_OWN_ALIASED=' + (A 1 'LPG_HMB' 'C1_C5_mass')
            'FEB_FALLBACK=' + (V 2 'FG_Internal' 'C6_C10_mass')
            'OWN_MASS_WINS=' + (V 3 'FG_Internal' 'C1_C5_mass')
            'ALIASES=' + $plan.Aliases
            'FALLBACKS=' + $plan.Fallbacks
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("C1C5=0.11", output, StringComparison.Ordinal);
        Assert.Contains("C6C10_OWN=0.22", output, StringComparison.Ordinal);      // справжній C6_C10, не C6_1_mass (0.44)
        Assert.Contains("C12C19=0.33", output, StringComparison.Ordinal);
        Assert.Contains("C1C5_RAW=0", output, StringComparison.Ordinal);          // показника без _mass у довіднику не лишається
        Assert.Contains("EXPORT_C1C5=0.55", output, StringComparison.Ordinal);
        Assert.Contains("EXPORT_C6_FALLBACK=0.66", output, StringComparison.Ordinal); // Export не має C6_C10: запасна, як у старій
        Assert.Contains("ALIASED=True", output, StringComparison.Ordinal);
        Assert.Contains("FALLBACK_NOT_ALIASED=False", output, StringComparison.Ordinal);
        Assert.Contains("HMB_OWN_ALIASED=False", output, StringComparison.Ordinal);
        Assert.Contains("FEB_FALLBACK=0.5", output, StringComparison.Ordinal);    // C6_C10 порожній -> C6_1_mass
        Assert.Contains("OWN_MASS_WINS=2.5", output, StringComparison.Ordinal);   // власна `_mass` старша за псевдонім
        Assert.Contains("ALIASES=4", output, StringComparison.Ordinal);           // C1_C5, C6_C10, C12_C19 (FGI) + FG_Export C1_C5
        Assert.Contains("FALLBACKS=3", output, StringComparison.Ordinal);         // FG_Export і FG_Makat (січень), FG_Internal (лютий)
    }

    /// <summary>
    /// Зміна наявного запису - batch upsert за <c>id</c> БЕЗ code/name (код наявного запису = ECR-REG-0409
    /// entryCodeTaken, баг першого -RewriteAliased на :5094). Функції беруться з самого скрипта (AST) із підставним
    /// Invoke-Ecr: перевіряється форма запиту, відмова (Rejected > 0 -> код виходу скрипта 1) і ідемпотентність.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перезапис_наявного_запису_йде_за_id_без_code_відмова_лічиться_повтор_без_змін()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            # EcrApi.ps1 вмикає StrictMode - так було на :5094: доступ до відсутньої властивості рядка кидає виняток.
            Set-StrictMode -Version Latest
            $text = Get-Content -Raw -LiteralPath $args[0]
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$null)
            $fns = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and ($n.Name -eq 'Add-HseEntries' -or $n.Name -eq 'New-HseCompositionValues' -or $n.Name -eq 'Get-HseProp') }, $true)
            foreach ($f in $fns) { Invoke-Expression $f.Extent.Text }
            $BatchSize = 200
            $script:calls = New-Object System.Collections.Generic.List[object]
            $script:mode = 'ok'
            function Assert-EcrOk($r, $what) { }
            function Invoke-Ecr {
                param($Method, $Path, $Body, $Headers)
                $script:calls.Add([pscustomobject]@{ Path = $Path; Items = @($Body.items) })
                $n = @($Body.items).Count
                if ($script:mode -eq 'conflict') {
                    return [pscustomobject]@{ Json = [pscustomobject]@{ applied = $false; added = 0; updated = 0; unchanged = 0
                            rows = @([pscustomobject]@{ status = 'error'; errors = @([pscustomobject]@{ errorCode = 'ECR-REG-0409' }) }) } }
                }
                $up = if ($script:mode -eq 'repeat') { 0 } else { 1 }
                return [pscustomobject]@{ Json = [pscustomobject]@{ applied = $true; added = 0; updated = $up; unchanged = $n - $up; rows = @() } }
            }
            $item = [pscustomobject]@{ Year = 2026; Month = 1; Stream = 'FG_Internal'; Indicator = 'C6_C10_mass'; Value = '0.22' }
            $vals = New-HseCompositionValues $item
            $rows = @(
                [pscustomobject]@{ ClientRowId = 'A'; Id = 4711; Values = $vals },
                [pscustomobject]@{ ClientRowId = 'B'; Id = 4712; Values = $vals })

            $r = Add-HseEntries 'LAND_HSE400_COMPOSITION' $rows
            $first = $script:calls[0]
            'PATH_DRY_FALSE=' + $first.Path.EndsWith('dryRun=false')
            'HAS_ID=' + $first.Items[0].ContainsKey('id')
            'ID=' + $first.Items[0]['id']
            'HAS_CODE=' + $first.Items[0].ContainsKey('code')
            'HAS_NAME=' + $first.Items[0].ContainsKey('name')
            'VALUE=' + $first.Items[0]['values']['VALUE']
            'UPDATED=' + $r.Updated
            'REJECTED=' + $r.Rejected

            $script:mode = 'repeat'
            $r2 = Add-HseEntries 'LAND_HSE400_COMPOSITION' $rows
            'REPEAT_UPDATED=' + $r2.Updated
            'REPEAT_UNCHANGED=' + $r2.Unchanged
            'REPEAT_REJECTED=' + $r2.Rejected

            $script:mode = 'conflict'
            $r3 = Add-HseEntries 'LAND_HSE400_COMPOSITION' $rows 3>$null
            'CONFLICT_REJECTED=' + $r3.Rejected

            $script:mode = 'ok'
            $newRows = @([pscustomobject]@{ ClientRowId = 'N'; Code = 'H202601_X'; Display = 'H202601_X'; Values = $vals })
            $null = Add-HseEntries 'LAND_HSE400_COMPOSITION' $newRows
            $last = $script:calls[$script:calls.Count - 1]
            'NEW_HAS_CODE=' + $last.Items[0].ContainsKey('code')
            'NEW_HAS_ID=' + $last.Items[0].ContainsKey('id')

            # Змішаний вхід під StrictMode: нові (без Id) і наявні (без Code/Display) в одному виклику.
            $mixed = @($newRows[0], $rows[0])
            $null = Add-HseEntries 'LAND_HSE400_COMPOSITION' $mixed
            $mi = $script:calls[$script:calls.Count - 1].Items
            'MIXED_COUNT=' + $mi.Count
            'MIXED_NEW_CODE=' + $mi[0].ContainsKey('code')
            'MIXED_EXISTING_ID=' + $mi[1]['id']
            'MIXED_EXISTING_HAS_CODE=' + $mi[1].ContainsKey('code')

            $null = Add-HseEntries 'LAND_HSE400_COMPOSITION' $rows -ServerDryRun
            'DRY_PATH=' + $script:calls[$script:calls.Count - 1].Path.EndsWith('dryRun=true')
            """;

        var (exit, output) = RunPowerShell(script, LandFile("Import-LandHse400Composition.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("PATH_DRY_FALSE=True", output, StringComparison.Ordinal);
        Assert.Contains("HAS_ID=True", output, StringComparison.Ordinal);
        Assert.Contains("ID=4711", output, StringComparison.Ordinal);
        Assert.Contains("HAS_CODE=False", output, StringComparison.Ordinal);   // код наявного запису = 409 entryCodeTaken
        Assert.Contains("HAS_NAME=False", output, StringComparison.Ordinal);   // назву/вікно наявному не задають
        Assert.Contains("VALUE=0.22", output, StringComparison.Ordinal);
        Assert.Contains("UPDATED=1", output, StringComparison.Ordinal);
        Assert.Contains("REJECTED=0", output, StringComparison.Ordinal);
        Assert.Contains("REPEAT_UPDATED=0", output, StringComparison.Ordinal);  // повтор - 0 змін
        Assert.Contains("REPEAT_UNCHANGED=2", output, StringComparison.Ordinal);
        Assert.Contains("REPEAT_REJECTED=0", output, StringComparison.Ordinal);
        Assert.Contains("CONFLICT_REJECTED=2", output, StringComparison.Ordinal); // відмова пакета -> exit 1 скрипта
        Assert.Contains("NEW_HAS_CODE=True", output, StringComparison.Ordinal);
        Assert.Contains("NEW_HAS_ID=False", output, StringComparison.Ordinal);
        Assert.Contains("MIXED_COUNT=2", output, StringComparison.Ordinal);
        Assert.Contains("MIXED_NEW_CODE=True", output, StringComparison.Ordinal);
        Assert.Contains("MIXED_EXISTING_ID=4711", output, StringComparison.Ordinal);
        Assert.Contains("MIXED_EXISTING_HAS_CODE=False", output, StringComparison.Ordinal);
        Assert.Contains("DRY_PATH=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_має_RewriteAliased_який_перезаписує_лише_псевдоніми_і_лише_за_підтвердженням_стенда()
    {
        var text = File.ReadAllText(LandFile("Import-LandHse400Composition.ps1"));

        Assert.Contains("[switch]$RewriteAliased", text, StringComparison.Ordinal);
        Assert.Contains("$_.Aliased -and $haveComp.ContainsKey(", text, StringComparison.Ordinal);
        Assert.Contains("if ($failed) { exit 1 }", text, StringComparison.Ordinal);
        // Мутація (як і решта) - після Set-EcrStandConfirmation; сам перезапис - лише за прапорцем.
        var confirm = text.IndexOf("Set-EcrStandConfirmation", StringComparison.Ordinal);
        var rewrite = text.IndexOf("$rewriteComp = if ($RewriteAliased)", StringComparison.Ordinal);
        Assert.True(confirm >= 0 && confirm < rewrite, "Підтвердження стенда - до планування перезапису.");
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
