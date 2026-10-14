using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-423 (Stage Template): місячні колонки <c>OperatingTimeReleaseOnColdVentYesNo_Mnn</c> (T9_04/T9_07) у моделі String, а в
/// чернетці-клоні лишались Decimal(34,16): API тип не змінює (<c>ECR-TMPL-0422</c>), а <c>Fill-StructureSql</c> вмів лише
/// Decimal/Int→Formula і розширення Precision. Тести виконують справжні <c>Get-LandTextRetypePlan</c>,
/// <c>Get-LandColumnTypeMismatches</c> і гілку <c>Fill-StructureSql</c> з підробленими <c>Sql-Query</c>/<c>Sql-Exec</c>
/// (БД немає; підроблений <c>Sql-Exec</c> відтворює предикати SQL: Status=0, DataType=2, немає значень комірок).
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>Get-LandTextRetypePlan</c> замінити умову <c>-ne $dt.Decimal</c> на <c>-eq $dt.Int</c>
/// або прибрати <c>v.Status=0</c> з UPDATE у Build - відповідні тести червоніють.
/// </remarks>
public sealed class LandTemplateTextRetypeTests
{
    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        . $args[0]
        $build = $args[1]; $mode = $args[2]
        function Get-Fn($path, $name) {
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
            $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq $name }, $true)
            if (-not $fn) { throw "$name не знайдено" }
            return $fn.Extent.Text
        }
        $modelJson = '{"sheets":[{"code":"S","nameEn":"s","ordinal":1,"mandatory":true,"visible":true,"tables":[{"code":"T","columns":[
            {"code":"M01","dataType":"String","month":1},
            {"code":"M02","dataType":"String","month":2},
            {"code":"M03","dataType":"String"},
            {"code":"XI","dataType":"String"},
            {"code":"NUM","dataType":"Formula","precision":18,"scale":2,"expression":"[M03] + 1"},
            {"code":"BR","dataType":"Formula","precision":null,"expression":"[Period].Sequence = 1 ? [M01] : [M02]"},
            {"code":"BR2","dataType":"Formula","precision":null,"expression":"[M01]"},
            {"code":"BRNUM","dataType":"Formula","precision":null,"expression":"[Other] + 1"},
            {"code":"DEC","dataType":"Decimal","precision":34,"scale":16}]}]}]}'
        if ($mode -eq 'plan') {
            $mdl = $modelJson | ConvertFrom-Json
            $ex = @{
                M01 = @{ id = 1; type = 2; precision = 34; scale = 16 }
                M02 = @{ id = 2; type = 0; precision = $null; scale = $null }
                M03 = @{ id = 3; type = 2; precision = 34; scale = 16 }
                XI = @{ id = 4; type = 1; precision = $null; scale = $null }
                BR = @{ id = 6; type = 6; precision = 34; scale = 16 }
                BR2 = @{ id = 7; type = 6; precision = $null; scale = $null }
                BRNUM = @{ id = 8; type = 6; precision = 34; scale = 16 }
                DEC = @{ id = 9; type = 2; precision = 34; scale = 16 }
            }
            $p = Get-LandTextRetypePlan @($mdl.sheets[0].tables[0].columns) $ex
            Write-Host ('RETYPE=' + (@($p.Retype | ForEach-Object { $_.Code }) -join ','))
            Write-Host ('BRIDGES=' + (@($p.Bridges | ForEach-Object { $_.Code }) -join ','))
            Write-Host ('WARN=' + @($p.Warn).Count)
            foreach ($w in @($p.Warn)) { Write-Host ('W=' + $w) }
            Write-Host ('SAME=' + $p.Same)
        }
        elseif ($mode -eq 'types') {
            $mdl = '{"sheets":[{"code":"S","tables":[{"code":"T","columns":[
                {"code":"A","dataType":"String"},{"code":"B","dataType":"String"},{"code":"C","dataType":"Decimal"},{"code":"D","dataType":"Formula"},{"code":"E","dataType":"Int"}]}]}]}' | ConvertFrom-Json
            $st = '{"sheets":[{"code":"S","tables":[{"code":"T","columns":[
                {"code":"A","dataType":"Decimal"},{"code":"B","dataType":"String"},{"code":"C","dataType":"Decimal"},{"code":"D","dataType":"6"}]}]}]}' | ConvertFrom-Json
            foreach ($l in @(Get-LandColumnTypeMismatches $st $mdl)) { Write-Host ('BAD=' + $l) }
        }
        elseif ($mode -eq 'sources') {
            $mdl = Get-Content -LiteralPath $args[3] -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($k in @(Get-LandTextBridgeSources $mdl)) { Write-Host ('SRC=' + $k) }
            foreach ($b in @(Get-LandTextBridgeColumns $mdl)) { Write-Host ('BR=' + $b.Table + '/' + $b.Code) }
        }
        else {
            # Fill-StructureSql із підробленим SQL; $mode = published | draft | values
            $script:Mdl = $modelJson | ConvertFrom-Json
            $script:Status = if ($mode -eq 'published') { 1 } else { 0 }
            $script:HasValues = @{}
            if ($mode -eq 'values') { $script:HasValues[2] = $true }
            $script:State = @{
                1 = @{ code = 'M01'; type = 2; p = 34; s = 16; u = 7 }
                2 = @{ code = 'M02'; type = 2; p = 34; s = 16; u = 7 }
                3 = @{ code = 'M03'; type = 2; p = 34; s = 16 }
                4 = @{ code = 'XI'; type = 1; p = $null; s = $null }
                5 = @{ code = 'NUM'; type = 6; p = 18; s = 2 }
                6 = @{ code = 'BR'; type = 6; p = 34; s = 16; u = 5 }
                7 = @{ code = 'BR2'; type = 6; p = 34; s = 16 }
                8 = @{ code = 'BRNUM'; type = 6; p = 34; s = 16 }
                9 = @{ code = 'DEC'; type = 2; p = 34; s = 16 }
            }
            $DataTypeCode = @{ String = 0; Int = 1; Decimal = 2; Bool = 3; Date = 4; Lookup = 5; Formula = 6; Unit = 7; Calculated = 8 }
            $effectiveSqlConnection = 'fake'
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
                    [void]$dt.Columns.Add('Id', [int]); [void]$dt.Columns.Add('Code', [string]); [void]$dt.Rows.Add(1, 'S')
                }
                elseif ($sql -like '*FROM cfg.TableDef t JOIN cfg.SheetDef*') {
                    [void]$dt.Columns.Add('Id', [int]); [void]$dt.Columns.Add('S', [string]); [void]$dt.Columns.Add('T', [string]); [void]$dt.Columns.Add('Mx', [object])
                    [void]$dt.Rows.Add(21, 'S', 'T', [DBNull]::Value)
                }
                elseif ($sql -like '*FROM cfg.ColumnDef c*') {
                    foreach ($n in 'Id:int', 'TableDefId:int', 'Code:string', 'DataType:int') { $q = $n -split ':'; [void]$dt.Columns.Add($q[0], $(if ($q[1] -eq 'int') { [int] } else { [string] })) }
                    [void]$dt.Columns.Add('Precision', [object]); [void]$dt.Columns.Add('Scale', [object])
                    foreach ($k in ($script:State.Keys | Sort-Object)) {
                        $c = $script:State[$k]
                        $pp = if ($null -eq $c.p) { [DBNull]::Value } else { [int]$c.p }
                        $ss = if ($null -eq $c.s) { [DBNull]::Value } else { [int]$c.s }
                        [void]$dt.Rows.Add([int]$k, 21, $c.code, [int]$c.type, $pp, $ss)
                    }
                }
                else { throw 'STOP-AFTER' }
                return , $dt
            }
            function Sql-Exec($cn, [string]$sql, [hashtable]$p = @{}) {
                if ($sql -like 'UPDATE c SET DataType=0, Precision=NULL*') {
                    $c = $script:State[[int]$p.i]
                    if ($script:Status -eq 0 -and $c.type -eq 2 -and -not $script:HasValues.ContainsKey([int]$p.i)) {
                        if ($sql -notlike '*UnitId=NULL*') { return 0 }; $c.type = 0; $c.p = $null; $c.s = $null; $c.u = $null; Write-Host ('RETYPED=' + $c.code); return 1
                    }
                    return 0
                }
                if ($sql -like 'UPDATE c SET Precision=NULL, Scale=NULL*') {
                    $c = $script:State[[int]$p.i]
                    if ($script:Status -eq 0 -and $c.type -in 6, 8 -and ($null -ne $c.p -or $null -ne $c.s)) {
                        $c.p = $null; $c.s = $null; Write-Host ('BRIDGE=' + $c.code); return 1
                    }
                    return 0
                }
                if ($sql -like 'UPDATE cfg.ColumnDef SET Precision=@p*') { Write-Host ('WIDEN=' + $script:State[[int]$p.i].code); return 1 }
                if ($sql -like 'UPDATE cfg.ColumnDef SET DataType=@t, IsReadOnly=1*') { Write-Host ('TOFORMULA=' + $script:State[[int]$p.i].code); return 1 }
                return 0
            }
            . ([scriptblock]::Create((Get-Fn $build 'Fill-StructureSql')))
            foreach ($run in 1, 2) {
                Write-Host ('RUN=' + $run)
                try { Fill-StructureSql 7 } catch { Write-Host ('END=' + $_) }
            }
            foreach ($k in ($script:State.Keys | Sort-Object)) { $c = $script:State[$k]; Write-Host ('STATE=' + $c.code + ':' + $c.type + ':' + $c.p + ':' + $c.s + ':u' + $c.u) }
        }
        """;

    private static string Run(string mode, string? extra = null)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-retype-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, Harness, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var arguments = new List<string>
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file,
                LandFile("LandTemplateDiff.Lib.ps1"), LandFile("Build-LandTemplate.ps1"), mode,
            };
            if (extra is not null)
            {
                arguments.Add(extra);
            }

            foreach (var argument in arguments)
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

    private static string[] Lines(string output, string prefix)
        => output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_ретайпить_лише_Decimal_у_String_і_скидає_precision_лише_текстових_мостів()
    {
        var output = Run("plan");

        Assert.Contains("RETYPE=M01\n", output.Replace("\r", string.Empty), StringComparison.Ordinal);   // M02 вже String, M03 читає числова формула, XI - Int
        Assert.Contains("BRIDGES=BR\n", output.Replace("\r", string.Empty), StringComparison.Ordinal);   // BR2 вже без межі, BRNUM читає не String
        Assert.Single(Lines(output, "SAME="), "SAME=1");
        var warn = Lines(output, "W=");
        Assert.Equal(2, warn.Length);
        Assert.Single(warn, w => w.StartsWith("W=M03:", StringComparison.Ordinal) && w.Contains("NUM", StringComparison.Ordinal));
        Assert.Single(warn, w => w.StartsWith("W=XI:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_у_чернетці_ретайпить_колонку_і_міст_а_повтор_дає_нуль_змін()
    {
        var output = Run("draft");
        var runs = output.Split("RUN=2", StringSplitOptions.None);

        Assert.Equal(["RETYPED=M01", "RETYPED=M02"], Lines(runs[0], "RETYPED="));
        Assert.Equal(["BRIDGE=BR", "BRIDGE=BR2"], Lines(runs[0], "BRIDGE="));   // BR2 мав 34/16 у клоні
        Assert.Matches(@"LOG=.*Decimal->String.*: 2\s", runs[0]);
        Assert.Contains("LOG=    Decimal->String: S/T/M01", runs[0], StringComparison.Ordinal);
        Assert.Contains("LOG=    міст precision=NULL: S/T/BR", runs[0], StringComparison.Ordinal);

        // повтор - нуль змін
        Assert.Empty(Lines(runs[1], "RETYPED="));
        Assert.Empty(Lines(runs[1], "BRIDGE="));
        Assert.Matches(@"LOG=.*Decimal->String.*: 0\s", runs[1]);

        // стан: M01/M02 String без меж; M03 (читає числова формула) і DEC лишились Decimal; XI - Int; BRNUM не чіпаний
        Assert.Contains("STATE=M01:0:::u", output, StringComparison.Ordinal);
        Assert.Contains("STATE=M02:0:::u", output, StringComparison.Ordinal);
        Assert.Contains("STATE=M03:2:34:16", output, StringComparison.Ordinal);
        Assert.Contains("STATE=XI:1::", output, StringComparison.Ordinal);
        Assert.Contains("STATE=BR:6:::u5", output, StringComparison.Ordinal);
        Assert.Contains("STATE=BR2:6::", output, StringComparison.Ordinal);
        Assert.Contains("STATE=BRNUM:6:34:16", output, StringComparison.Ordinal);
        Assert.Contains("STATE=DEC:2:34:16", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_не_чіпає_опубліковану_версію_і_колонку_зі_значеннями()
    {
        var published = Run("published");
        Assert.Empty(Lines(published, "RETYPED="));
        Assert.Empty(Lines(published, "BRIDGE="));
        Assert.Contains("STATE=M01:2:34:16:u7", published, StringComparison.Ordinal);
        Assert.Contains("LOG=  WARN тип колонки: S/T/M01: UPDATE не застосовано", published, StringComparison.Ordinal);

        var withValues = Run("values");
        Assert.Equal(["RETYPED=M01"], Lines(withValues.Split("RUN=2", StringSplitOptions.None)[0], "RETYPED="));
        Assert.Contains("STATE=M02:2:34:16:u7", withValues, StringComparison.Ordinal);   // M02 має значення - не чіпаємо
        Assert.Contains("S/T/M02: UPDATE не застосовано", withValues, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_порівнює_тип_колонок_і_модель_дає_джерела_текстових_мостів_T9_04_T9_07()
    {
        var types = Lines(Run("types"), "BAD=");
        Assert.Single(types);
        Assert.StartsWith("BAD=S/T: тип відрізняється: A String!=Decimal", types[0], StringComparison.Ordinal);
        Assert.DoesNotContain("D ", types[0], StringComparison.Ordinal);   // "6" розпізнано як Formula

        var model = Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        var sources = Run("sources", model);
        var src = Lines(sources, "SRC=");
        Assert.Equal(36, src.Length);   // T9_04 + T9_07 (24, Decimal у клоні 1.0.7.0) і T10_03 GasConsumption_Mnn (12, уже String)
        Assert.Equal(12, src.Count(s => s.Contains("/T9_04/OperatingTimeReleaseOnColdVentYesNo_M", StringComparison.Ordinal)));
        Assert.Equal(12, src.Count(s => s.Contains("/T9_07/OperatingTimeReleaseOnColdVentYesNo_M", StringComparison.Ordinal)));
        Assert.Contains(Lines(sources, "BR="), b => b == "BR=T9_04/Land_Measure_OperatingTime__ECW_C11_02");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void DryRun_друкує_кандидатів_а_Verify_викликає_порівняння_типів_і_скрипти_мають_BOM()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var dry = build.IndexOf("if ($DryRun) {", StringComparison.Ordinal);
        var ret = build.IndexOf("звернень до API немає.'; return", StringComparison.Ordinal);
        Assert.True(dry > 0 && ret > dry);
        Assert.Contains("Get-LandTextBridgeSources $Mdl", build[dry..ret], StringComparison.Ordinal);

        // SQL-гарди ретайпу: лише чернетка, лише Decimal, без значень комірок
        var fill = build[build.IndexOf("function Fill-StructureSql", StringComparison.Ordinal)..];
        Assert.Contains("v.Status=0 AND c.DataType=2 AND NOT EXISTS (SELECT 1 FROM doc.CellValue", fill, StringComparison.Ordinal);
        Assert.Contains("DisplayFormat=NULL, UnitId=NULL FROM cfg.ColumnDef c", fill, StringComparison.Ordinal);

        var verify = File.ReadAllText(LandFile("Verify-LandTemplate.ps1"));
        Assert.Contains("текстова колонка", verify, StringComparison.Ordinal);
        Assert.Contains("Get-LandColumnTypeMismatches $st $mdl", verify, StringComparison.Ordinal);
        Assert.Contains("Get-LandTextBridgeColumns $mdl", verify, StringComparison.Ordinal);

        foreach (var name in new[] { "Build-LandTemplate.ps1", "Verify-LandTemplate.ps1", "LandTemplateDiff.Lib.ps1" })
        {
            var head = File.ReadAllBytes(LandFile(name)).Take(3).ToArray();
            Assert.True(head.SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), $"{name}: немає UTF-8 BOM.");
        }
    }
}
