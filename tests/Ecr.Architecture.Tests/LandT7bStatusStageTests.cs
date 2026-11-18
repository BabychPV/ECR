using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B26 (P-0118-25, D-425): етап <c>Build-LandTemplate.ps1 -Stage T7bStatus</c> пише статус Running у рядки речовин T7b_00
/// НАЯВНИХ документів (колонка Land_Status нова й порожня, без неї фільтр «лише Running» виключив би всі рядки). Чиста
/// логіка — <c>tools/land/LandT7bStatus.Lib.ps1</c>; тут вона й сам етап (мережу підмінено) виконуються справжнім PowerShell
/// на пресетах із <c>land-template.model.json</c>.
/// </summary>
/// <remarks>
/// Мутація (m3, лише локально): у <c>Get-LandStatusPresetPlan</c> не додавати рядок у план (<c>$patch.Add</c>) — червоніють
/// «наявний документ» і «етап пише»; рахунок у розрахунку — <c>Ecr.Calculations.Tests.LandT7bVolumeCalculationTests</c>
/// (до етапу жоден рядок не рахується, після — рядки речовин).
/// </remarks>
public sealed class LandT7bStatusStageTests
{
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $root = $args[0]
        $land = Join-Path (Join-Path $root 'tools') 'land'
        . (Join-Path $land 'LandT7bStatus.Lib.ps1')
        $modelPath = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $root 'docs') 'delivery') 'reference-data') 'land-template') 'land-template.model.json'
        $mdlAll = [IO.File]::ReadAllText($modelPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
        $t7 = @($mdlAll.sheets | ForEach-Object { $_.tables } | Where-Object { $_.code -eq 'T7b_00' })[0]
        $pre = $t7.statusPresets
        function New-T7Row([string]$k, $status) {
            $cells = [pscustomobject]@{ January = 2.5 }
            if ($null -ne $status) { $cells | Add-Member -NotePropertyName 'Land_Status' -NotePropertyValue $status }
            return [pscustomobject]@{ rowKey = $k; rowVersion = "v-$k"; cells = $cells }
        }
        function Show($p) { return ('W={0}|K={1}|M={2}|E={3}|EV={4}|ROWS={5}' -f $p.Write, $p.Kept, $p.Missing, $p.EmptyRows, $p.EmptyWithValue, ((@($p.Rows | ForEach-Object { "$($_.rowKey):$($_.baseVersion):$(@($_.cells)[0].columnCode)=$(@($_.cells)[0].value)" })) -join ',')) }

        """;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void План_пише_Running_лише_туди_де_статусу_немає_S3_9_ніколи_повтор_нуль()
    {
        var output = Run("""
            Write-Host ('PRE=' + $pre.column + '|' + $pre.entry + '|' + (@($pre.rows) -join ',') + '|' + (@($pre.emptyRows) -join ','))
            # наявний документ: колонка нова, статусу немає ніде
            $p1 = Get-LandStatusPresetPlan -Presets $pre -Rows @((New-T7Row 'S1_6' $null), (New-T7Row 'S2_7' $null), (New-T7Row 'S3_9' $null)) -EntryId '501'
            Write-Host ('EXIST=' + (Show $p1))
            # людина поставила S2_7 = Removed (id 502) і порожній рядок '' у S1_6 - перше не чіпаємо, друге пишемо
            $p2 = Get-LandStatusPresetPlan -Presets $pre -Rows @((New-T7Row 'S1_6' ''), (New-T7Row 'S2_7' 502), (New-T7Row 'S3_9' $null)) -EntryId '501'
            Write-Host ('USER=' + (Show $p2))
            # повтор після запису: 0
            $p3 = Get-LandStatusPresetPlan -Presets $pre -Rows @((New-T7Row 'S1_6' 501), (New-T7Row 'S2_7' 501), (New-T7Row 'S3_9' $null)) -EntryId '501'
            Write-Host ('AGAIN=' + (Show $p3))
            # хтось поставив статус у S3_9: не пишемо й не стираємо, лише рахуємо
            $p4 = Get-LandStatusPresetPlan -Presets $pre -Rows @((New-T7Row 'S1_6' 501), (New-T7Row 'S2_7' 501), (New-T7Row 'S3_9' ([pscustomobject]@{ id = 501 }))) -EntryId '501'
            Write-Host ('S39=' + (Show $p4))
            # таблиця без рядка
            $p5 = Get-LandStatusPresetPlan -Presets $pre -Rows @((New-T7Row 'S1_6' $null)) -EntryId '501'
            Write-Host ('MISS=' + (Show $p5))
            """);

        Assert.Contains("PRE=Land_Status|RUNNING|S1_6,S2_7|S3_9\n", output, StringComparison.Ordinal);
        Assert.Contains("EXIST=W=2|K=0|M=0|E=1|EV=0|ROWS=S1_6:v-S1_6:Land_Status=501,S2_7:v-S2_7:Land_Status=501\n", output, StringComparison.Ordinal);
        Assert.Contains("USER=W=1|K=1|M=0|E=1|EV=0|ROWS=S1_6:v-S1_6:Land_Status=501\n", output, StringComparison.Ordinal);
        Assert.Contains("AGAIN=W=0|K=2|M=0|E=1|EV=0|ROWS=\n", output, StringComparison.Ordinal);
        Assert.Contains("S39=W=0|K=2|M=0|E=1|EV=1|ROWS=\n", output, StringComparison.Ordinal);
        Assert.Contains("MISS=W=1|K=0|M=1|E=0|EV=0|ROWS=S1_6:v-S1_6:Land_Status=501\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "B26")]
    public void Етап_T7bStatus_пише_наявні_документи_а_DryRun_нічого()
    {
        var output = Run("""
            $build = Join-Path $land 'Build-LandTemplate.ps1'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($build, [ref]$null, [ref]$null)
            $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-T7bStatus' }, $true)
            if (-not $fn) { throw 'Stage-T7bStatus не знайдено' }
            $vs = $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.AttributeAst] -and $a.TypeName.Name -eq 'ValidateSet' }, $true) |
                Where-Object { $_.Extent.Text -match "'RowFilter'" } | Select-Object -First 1
            Write-Host ('VALIDATESET=' + ($vs.Extent.Text -match "'T7bStatus'"))
            Write-Host ('WIRED=' + ([IO.File]::ReadAllText($build) -match "Stage -contains 'T7bStatus'\) \{ Stage-T7bStatus \}"))
            $global:Mdl = $mdlAll; $global:patched = @()
            $BuildUser = 'u'; $ProjectCode = 'P'; $Year = 2026
            function Log([string]$m) { Write-Host ('LOG=' + $m) }
            function LoginAs([string]$u) { }
            function RegEntryId([string]$reg, [string]$name) { if ($reg -ne 'LAND_GENERIC_EQUIPSTATUS' -or $name -ne 'code:RUNNING') { throw "entry $reg $name" }; return 501 }
            function Items($j) { return @($j) }
            function TableInstances([int]$docId, [int]$period) { if ($period -eq 202609) { return @{ 'S07B_WATER_MEASUREMENTS/T7b_00' = 70 } }; return @{} }
            function Api([string]$Method, [string]$Path) {
                if ($Path -eq '/api/v1/projects') { return [pscustomobject]@{ Json = @([pscustomobject]@{ code = 'P'; id = 5 }) } }
                if ($Path -like '/api/v1/documents?projectId=5*') { return [pscustomobject]@{ Json = @([pscustomobject]@{ id = 96 }) } }
                return [pscustomobject]@{ Json = [pscustomobject]@{ rows = @((New-T7Row 'S1_6' $null), (New-T7Row 'S2_7' $null), (New-T7Row 'S3_9' $null)) } }
            }
            function Patch-Rows([int]$docId, [int]$period, [int]$ti, $rows, [string]$what) { $global:patched += , @($rows | ForEach-Object { "$docId/$period/${ti}:$($_.rowKey)=$(@($_.cells)[0].value)" }); return @($rows).Count }
            . ([scriptblock]::Create($fn.Extent.Text))
            $DryRun = $true
            Stage-T7bStatus
            Write-Host ('DRY=' + @($global:patched).Count)
            $DryRun = $false
            Stage-T7bStatus
            Write-Host ('WET=' + ((@($global:patched | ForEach-Object { $_ })) -join ','))
            """);

        Assert.Contains("VALIDATESET=True\n", output, StringComparison.Ordinal);
        Assert.Contains("WIRED=True\n", output, StringComparison.Ordinal);
        Assert.Contains("DRY=0\n", output, StringComparison.Ordinal);
        Assert.Contains("[DryRun: нічого не записано]", output, StringComparison.Ordinal);
        Assert.Contains("WET=96/202609/70:S1_6=501,96/202609/70:S2_7=501\n", output, StringComparison.Ordinal);
        Assert.Contains("записати 2, записано 2", output, StringComparison.Ordinal);
    }

    private static string Run(string body)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-t7bstatus-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Prelude + body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, SourceTree.Root })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 180 с.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert.True(process.ExitCode == 0, stdout + stderr);
            return stdout.Replace("\r", string.Empty);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
