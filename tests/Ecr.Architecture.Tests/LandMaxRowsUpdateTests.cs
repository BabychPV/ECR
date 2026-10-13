using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-UPG-1 (верифікація оновленого клону 13.10): <c>Build-LandTemplate.ps1</c> на клоні лише ВСТАВЛЯВ нові таблиці зі стелею
/// моделі, а <c>MaxDynamicRows</c> наявних лишав старим (T3_00=498, T9_04=184, T9_07=100) - сервер відхиляв рядки завантаження
/// (<c>ECR-ROW-0409</c>); <c>Verify-LandTemplate.ps1</c> стелі не порівнював. Тести виконують справжні функції у PowerShell:
/// чисті <c>Get-LandMaxRowsPlan</c>/<c>Get-LandMaxRowsMismatches</c> і гілку наявних таблиць <c>Fill-StructureSql</c> із
/// підробленими <c>Sql-Query</c>/<c>Sql-Exec</c>.
/// </summary>
public sealed class LandMaxRowsUpdateTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        . $args[0]
        $build = $args[1]; $mode = $args[2]
        function Get-Fn($path, $name) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
            $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name }, $true)
            if (-not $fn) { throw "$name не знайдено" }
            return $fn.Extent.Text
        }
        if ($mode -eq 'plan') {
            $wanted = @(
                [pscustomobject]@{ Key = 'S/LOW'; MaxRows = 1000 },
                [pscustomobject]@{ Key = 'S/HIGH'; MaxRows = 500 },
                [pscustomobject]@{ Key = 'S/EQ'; MaxRows = 250 },
                [pscustomobject]@{ Key = 'S/NOLIMIT'; MaxRows = 100 },
                [pscustomobject]@{ Key = 'S/NOWANT'; MaxRows = $null },
                [pscustomobject]@{ Key = 'S/NEW'; MaxRows = 700 }
            )
            $existing = @{
                'S/LOW' = @{ Id = 1; MaxRows = 498 }
                'S/HIGH' = @{ Id = 2; MaxRows = 900 }
                'S/EQ' = @{ Id = 3; MaxRows = 250 }
                'S/NOLIMIT' = @{ Id = 4; MaxRows = $null }
                'S/NOWANT' = @{ Id = 5; MaxRows = 10 }
            }
            $p = Get-LandMaxRowsPlan $wanted $existing
            Write-Host ('RAISE=' + (@($p.Raise | ForEach-Object { '{0}:{1}>{2}' -f $_.Key, $_.From, $_.To }) -join ';'))
            Write-Host ('SAME=' + $p.Same)
            foreach ($u in @($p.Raise)) { $existing[$u.Key] = @{ Id = $u.Id; MaxRows = $u.To } }
            $p2 = Get-LandMaxRowsPlan $wanted $existing
            Write-Host ('REPLAN=' + @($p2.Raise).Count)
            Write-Host ('HIGHKEPT=' + $existing['S/HIGH'].MaxRows)
        }
        elseif ($mode -eq 'verify') {
            $mdl = '{"sheets":[{"code":"S3","tables":[
                {"code":"T3_00","maxRows":1000},{"code":"T9_04","maxRows":500},{"code":"HIGH","maxRows":100},
                {"code":"EQ","maxRows":50},{"code":"NOLIMIT","maxRows":70},{"code":"NOWANT"},{"code":"ABSENT","maxRows":5}]}]}' | ConvertFrom-Json
            $st = '{"sheets":[{"code":"S3","tables":[
                {"code":"T3_00","maxDynamicRows":498},{"code":"T9_04","maxDynamicRows":184},{"code":"HIGH","maxDynamicRows":300},
                {"code":"EQ","maxDynamicRows":50},{"code":"NOLIMIT","maxDynamicRows":null},{"code":"NOWANT","maxDynamicRows":3}]}]}' | ConvertFrom-Json
            foreach ($l in @(Get-LandMaxRowsMismatches $st $mdl)) { Write-Host ('BAD=' + $l) }
        }
        else {
            # Fill-StructureSql: гілка НАЯВНИХ таблиць; зупиняємось на запиті колонок
            $script:Mdl = '{"sheets":[{"code":"S3","nameEn":"s","ordinal":1,"mandatory":true,"visible":true,"tables":[
                {"code":"T3_00","maxRows":1000},{"code":"T9_04","maxRows":500},{"code":"T9_07","maxRows":250},
                {"code":"HIGH","maxRows":100},{"code":"EQ","maxRows":50},{"code":"NOLIMIT","maxRows":70}]}]}' | ConvertFrom-Json
            $script:State = @{}
            $init = if ($mode -eq 'rerun') { @{ T3_00 = 1000; T9_04 = 500; T9_07 = 250; HIGH = 2000; EQ = 50; NOLIMIT = $null } }
                    else { @{ T3_00 = 498; T9_04 = 184; T9_07 = 100; HIGH = 2000; EQ = 50; NOLIMIT = $null } }
            $ids = @{ T3_00 = 11; T9_04 = 12; T9_07 = 13; HIGH = 14; EQ = 15; NOLIMIT = 16 }
            foreach ($k in $ids.Keys) { $script:State[[int]$ids[$k]] = $init[$k] }
            $SqlConnection = 'fake'
            function Log([string]$m) { Write-Host ('LOG=' + $m) }
            function Assert-EcrMutationAllowed($x) { }
            function Assert-LocalSqlConnection($x) { return $true }
            function Style-IdOf($r) { return $null }
            function Json-L10n($a, $b) { return '{}' }
            function New-Object {
                param($TypeName, $ArgumentList, [Parameter(ValueFromRemainingArguments = $true)]$Rest)
                $o = [pscustomobject]@{}
                foreach ($m in 'Open', 'Close', 'Dispose') { Add-Member -InputObject $o -MemberType ScriptMethod -Name $m -Value { } }
                return $o
            }
            function Sql-Query($cn, [string]$sql, [hashtable]$p = @{}) {
                $dt = [System.Data.DataTable]::new()
                if ($sql -like '*FROM cfg.SheetDef*') {
                    [void]$dt.Columns.Add('Id', [int]); [void]$dt.Columns.Add('Code', [string])
                    [void]$dt.Rows.Add(1, 'S3')
                }
                elseif ($sql -like '*FROM cfg.TableDef t JOIN cfg.SheetDef*') {
                    [void]$dt.Columns.Add('Id', [int]); [void]$dt.Columns.Add('S', [string]); [void]$dt.Columns.Add('T', [string]); [void]$dt.Columns.Add('Mx', [object])
                    foreach ($k in $ids.Keys) {
                        $v = $script:State[[int]$ids[$k]]
                        $mx = if ($null -eq $v) { [DBNull]::Value } else { [int]$v }
                        [void]$dt.Rows.Add([int]$ids[$k], 'S3', $k, $mx)
                    }
                }
                else { throw 'STOP-AT-COLUMNS' }
                return , $dt
            }
            function Sql-Exec($cn, [string]$sql, [hashtable]$p = @{}) {
                if ($sql -like 'UPDATE cfg.TableDef SET MaxDynamicRows=@m*') {
                    $cur = $script:State[[int]$p.t]
                    # той самий предикат, що й у SQL
                    if ($null -ne $cur -and [int]$cur -lt [int]$p.m) { $script:State[[int]$p.t] = [int]$p.m; Write-Host ('UPD=' + $p.t + '>' + $p.m); return 1 }
                    return 0
                }
                Write-Host ('OTHER=' + $sql)
                return 0
            }
            . ([scriptblock]::Create((Get-Fn $build 'Fill-StructureSql')))
            try { Fill-StructureSql 7 } catch { if ("$_" -notlike '*STOP-AT-COLUMNS*') { throw } }
            foreach ($k in ($ids.Keys | Sort-Object)) { Write-Host ('STATE=' + $k + ':' + $script:State[[int]$ids[$k]]) }
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-maxrows-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[]
                     {
                         "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
                         LandFile("LandTemplateDiff.Lib.ps1"), LandFile("Build-LandTemplate.ps1"), mode,
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
    public void План_піднімає_лише_нижчі_стелі_і_не_чіпає_вищі_рівні_та_без_стелі()
    {
        var output = Run("plan");

        Assert.Contains("RAISE=S/LOW:498>1000", output, StringComparison.Ordinal);
        Assert.DoesNotContain(";", output.Split('\n').First(l => l.StartsWith("RAISE=", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Contains("HIGHKEPT=900", output, StringComparison.Ordinal);
        Assert.Contains("REPLAN=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_ловить_стелю_нижчу_за_модель_і_не_чіпає_вищу_рівну_чи_відсутню()
    {
        var output = Run("verify");

        var bad = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("BAD=", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, bad.Count);
        Assert.Single(bad, l => l.StartsWith("BAD=S3/T3_00: ", StringComparison.Ordinal) && l.EndsWith("498 -> 1000", StringComparison.Ordinal));
        Assert.Single(bad, l => l.StartsWith("BAD=S3/T9_04: ", StringComparison.Ordinal) && l.EndsWith("184 -> 500", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_на_клоні_піднімає_стелі_наявних_таблиць_і_не_зменшує_вищі()
    {
        var output = Run("run1");

        Assert.Contains("STATE=T3_00:1000", output, StringComparison.Ordinal);
        Assert.Contains("STATE=T9_04:500", output, StringComparison.Ordinal);
        Assert.Contains("STATE=T9_07:250", output, StringComparison.Ordinal);
        Assert.Contains("STATE=HIGH:2000", output, StringComparison.Ordinal);
        Assert.Contains("STATE=EQ:50", output, StringComparison.Ordinal);
        Assert.Matches(@"STATE=NOLIMIT:\s*\r?\n", output);
        Assert.Equal(3, output.Split('\n').Count(l => l.StartsWith("UPD=", StringComparison.Ordinal)));
        Assert.Matches(@"LOG=.*MaxDynamicRows.*: 3\s", output);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_повторний_прогін_дає_нуль_змін()
    {
        var output = Run("rerun");

        Assert.DoesNotContain("UPD=", output, StringComparison.Ordinal);
        Assert.Matches(@"LOG=.*MaxDynamicRows.*: 0\s", output);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_скрипт_викликає_порівняння_стель_і_скрипти_land_мають_BOM()
    {
        var verify = File.ReadAllText(LandFile("Verify-LandTemplate.ps1"));
        Assert.Contains("Get-LandMaxRowsMismatches $st $mdl", verify, StringComparison.Ordinal);

        // PowerShell 5.1 без BOM читає UTF-8 як ANSI і псує кирилицю (див. LandScriptsHttpContractTests).
        foreach (var name in new[] { "Build-LandTemplate.ps1", "Verify-LandTemplate.ps1", "LandTemplateDiff.Lib.ps1" })
        {
            var head = File.ReadAllBytes(LandFile(name)).Take(3).ToArray();
            Assert.True(head.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), $"{name}: немає UTF-8 BOM.");
        }
    }
}
