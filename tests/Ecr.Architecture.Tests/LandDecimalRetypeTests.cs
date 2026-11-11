using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// AN-173 (шаблон Land 1.0.16.0): T9a_19 <c>Benzene</c>/<c>Xylene</c>/<c>Toluene</c>/<c>SaturatedHydrocarbonsC1C5</c>/<c>C6C10</c> -
/// джерела мостів <c>Land_MassFraction_0&lt;код&gt;</c> (<c>ECW_C11_11_04</c> type1) - у моделі Decimal(34,16) замість String
/// (у Strict текст × число = <c>#VALUE</c>). API тип колонки не змінює (<c>ECR-TMPL-0422</c>), тому <c>Fill-StructureSql</c>
/// ретайпить String→Decimal у чернетці-клоні SQL-ом за планом <c>Get-LandDecimalRetypePlan</c>. Тести виконують справжні
/// функції з підробленими <c>Sql-Query</c>/<c>Sql-Exec</c> (БД немає; підроблений <c>Sql-Exec</c> відтворює предикати SQL лише
/// тоді, коли вони є в тексті UPDATE: Status=0, DataType=0, IsMonthColumn=0, немає значень комірок).
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у моделі повернути одну з п'яти колонок у String; у <c>Get-LandDecimalRetypePlan</c>
/// прибрати перевірку <c>$c.month</c>; у UPDATE Build прибрати <c>v.Status=0</c> - відповідні тести червоніють.
/// </remarks>
public sealed class LandDecimalRetypeTests
{
    private static readonly string[] Sources = ["SaturatedHydrocarbonsC1C5", "SaturatedHydrocarbonsC6C10", "Benzene", "Xylene", "Toluene"];

    private static readonly (string Bridge, string Source)[] Bridges =
    [
        ("Land_MassFraction_0415", "SaturatedHydrocarbonsC1C5"),
        ("Land_MassFraction_0416", "SaturatedHydrocarbonsC6C10"),
        ("Land_MassFraction_0602", "Benzene"),
        ("Land_MassFraction_0616", "Xylene"),
        ("Land_MassFraction_0621", "Toluene"),
    ];

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static Dictionary<string, JsonElement> Columns(JsonDocument model, string table)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .Single(t => t.GetProperty("code").GetString() == table)
            .GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

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
            {"code":"BZ","dataType":"Decimal","precision":34,"scale":16},
            {"code":"PC","dataType":"Decimal","precision":34,"scale":16,"unit":"pct"},
            {"code":"MD","dataType":"Decimal","precision":34,"scale":16,"month":1},
            {"code":"DD","dataType":"Decimal","precision":34,"scale":16},
            {"code":"TX","dataType":"String"},
            {"code":"IN","dataType":"Decimal","precision":34,"scale":16},
            {"code":"NP","dataType":"Decimal"},
            {"code":"BRG","dataType":"Formula","readOnly":true,"precision":null,"scale":null,"expression":"[BZ]"}]}]}]}'
        if ($mode -eq 'plan') {
            $mdl = $modelJson | ConvertFrom-Json
            $ex = @{
                BZ = @{ id = 1; type = 0; precision = $null; scale = $null }
                PC = @{ id = 2; type = 0; precision = $null; scale = $null }
                MD = @{ id = 3; type = 0; precision = $null; scale = $null }
                DD = @{ id = 4; type = 2; precision = 34; scale = 16 }
                TX = @{ id = 5; type = 0; precision = $null; scale = $null }
                IN = @{ id = 6; type = 1; precision = $null; scale = $null }
                NP = @{ id = 7; type = 0; precision = $null; scale = $null }
                BRG = @{ id = 8; type = 6; precision = $null; scale = $null }
            }
            $p = Get-LandDecimalRetypePlan @($mdl.sheets[0].tables[0].columns) $ex
            Write-Host ('RETYPE=' + (@($p.Retype | ForEach-Object { '{0}#{1}:{2}/{3}:{4}' -f $_.Code, $_.Id, $_.Precision, $_.Scale, $_.Unit }) -join ','))
            foreach ($w in @($p.Warn)) { Write-Host ('W=' + $w) }
            Write-Host ('SAME=' + $p.Same)
        }
        else {
            # Fill-StructureSql із підробленим SQL; $mode = published | draft | values
            $script:Mdl = $modelJson | ConvertFrom-Json
            $script:Status = if ($mode -eq 'published') { 1 } else { 0 }
            $script:HasValues = @{}
            if ($mode -eq 'values') { $script:HasValues[2] = $true }
            $script:State = @{
                1 = @{ code = 'BZ'; type = 0; p = $null; s = $null; u = $null; m = $false; f = '@' }
                2 = @{ code = 'PC'; type = 0; p = $null; s = $null; u = $null; m = $false; f = '@' }
                3 = @{ code = 'MD'; type = 0; p = $null; s = $null; u = $null; m = $true; f = '@' }
                4 = @{ code = 'DD'; type = 2; p = 34; s = 16; u = $null; m = $false; f = '0.000000' }
                5 = @{ code = 'TX'; type = 0; p = $null; s = $null; u = $null; m = $false; f = '@' }
                6 = @{ code = 'IN'; type = 1; p = $null; s = $null; u = $null; m = $false; f = '@' }
                7 = @{ code = 'NP'; type = 0; p = $null; s = $null; u = $null; m = $false; f = '@' }
                8 = @{ code = 'BRG'; type = 6; p = $null; s = $null; u = $null; m = $false; f = '@' }
            }
            $DataTypeCode = @{ String = 0; Int = 1; Decimal = 2; Bool = 3; Date = 4; Lookup = 5; Formula = 6; Unit = 7; Calculated = 8 }
            $effectiveSqlConnection = 'fake'
            function Log([string]$m) { Write-Host ('LOG=' + $m) }
            function Assert-EcrMutationAllowed($x) { }
            function Assert-LocalSqlConnection($x) { return $true }
            function Style-IdOf($r) { return $null }
            function Json-L10n($a, $b) { return '{}' }
            function UnitId([string]$k) { if ($k -eq 'pct') { return 42 }; return $null }
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
                if ($sql -like 'UPDATE c SET DataType=2, Precision=@p*') {
                    $c = $script:State[[int]$p.i]
                    if ($sql -like '*v.Status=0*' -and $script:Status -ne 0) { return 0 }
                    if ($sql -like '*c.DataType=0*' -and $c.type -ne 0) { return 0 }
                    if ($sql -like '*c.IsMonthColumn=0*' -and $c.m) { return 0 }
                    if ($sql -like '*NOT EXISTS (SELECT 1 FROM doc.CellValue cv WHERE cv.ColumnDefId=c.Id)*' -and $script:HasValues.ContainsKey([int]$p.i)) { return 0 }
                    $c.type = 2; $c.p = [int]$p.p; $c.s = [int]$p.s
                    $c.u = if ($p.u -is [DBNull]) { $null } else { [int]$p.u }
                    if ($sql -like '*DisplayFormat=NULL*') { $c.f = $null }
                    Write-Host ('RETYPED=' + $c.code); return 1
                }
                if ($sql -like 'UPDATE c SET DataType=0*' -or $sql -like 'UPDATE c SET Precision=NULL*') { Write-Host ('TEXT=' + $script:State[[int]$p.i].code); return 1 }
                if ($sql -like 'UPDATE cfg.ColumnDef SET Precision=@p*') { Write-Host ('WIDEN=' + $script:State[[int]$p.i].code); return 1 }
                if ($sql -like 'UPDATE cfg.ColumnDef SET DataType=@t, IsReadOnly=1*') { Write-Host ('TOFORMULA=' + $script:State[[int]$p.i].code); return 1 }
                return 0
            }
            . ([scriptblock]::Create((Get-Fn $build 'Fill-StructureSql')))
            foreach ($run in 1, 2) {
                Write-Host ('RUN=' + $run)
                try { Fill-StructureSql 7 } catch { Write-Host ('END=' + $_) }
            }
            foreach ($k in ($script:State.Keys | Sort-Object)) { $c = $script:State[$k]; Write-Host ('STATE=' + $c.code + ':' + $c.type + ':' + $c.p + ':' + $c.s + ':u' + $c.u + ':f' + $c.f) }
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-decretype-{Guid.NewGuid():N}.ps1");
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

    private static string[] Lines(string output, string prefix)
        => output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Модель_T9a_19_джерела_MassFraction_Decimal_34_16_а_9a_16_і_мости_без_змін()
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var t19 = Columns(model, "T9a_19");
        var t16 = Columns(model, "T9a_16");

        foreach (var code in Sources)
        {
            var c = t19[code];
            Assert.True(c.GetProperty("dataType").GetString() == "Decimal", $"T9a_19.{code}: має бути Decimal (AN-173).");
            Assert.Equal(34, c.GetProperty("precision").GetInt32());
            Assert.Equal(16, c.GetProperty("scale").GetInt32());
            Assert.True(!c.TryGetProperty("month", out var m) || m.ValueKind == JsonValueKind.Null, $"T9a_19.{code}: не місячна.");

            // поза T9a_19 ті самі коди не чіпаються (AN-173 - лише 9a_19)
            Assert.Equal("String", t16[code].GetProperty("dataType").GetString());
            Assert.False(t16[code].TryGetProperty("precision", out _), $"T9a_16.{code}: precision не очікується.");
        }

        // мости лишаються як були (рішення щодо них окреме): Formula, лише читання, precision/scale null, вираз [джерело]
        foreach (var (bridge, source) in Bridges)
        {
            var b = t19[bridge];
            Assert.Equal("Formula", b.GetProperty("dataType").GetString());
            Assert.True(b.GetProperty("readOnly").GetBoolean());
            Assert.Equal(JsonValueKind.Null, b.GetProperty("precision").ValueKind);
            Assert.Equal(JsonValueKind.Null, b.GetProperty("scale").ValueKind);
            Assert.Equal($"[{source}]", b.GetProperty("expression").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Генератор_ретайпить_джерело_MassFraction_після_створення_мосту()
    {
        var gen = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "model-gen", "nae_group_bridges.py"));
        var bridge = gen.IndexOf("% (arg, arg, code, MASSFRACTION_METHODOLOGY), src_col['dataType']))", StringComparison.Ordinal);
        var retype = gen.IndexOf("_retype_decimal(src_col)", StringComparison.Ordinal);
        Assert.True(bridge > 0, "міст MassFraction бере тип джерела");
        Assert.True(retype > bridge, "ретайп джерела - ПІСЛЯ створення мосту (міст лишається текстовим, precision None)");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_String_в_Decimal_лише_для_немісячних_колонок_String_клону()
    {
        var output = Run("plan").Replace("\r", string.Empty);

        Assert.Contains("RETYPE=BZ#1:34/16:,PC#2:34/16:pct\n", output, StringComparison.Ordinal);   // MD місячна, TX у моделі String, DD уже Decimal, IN - Int
        Assert.Single(Lines(output, "SAME="), "SAME=1");
        var warn = Lines(output, "W=");
        Assert.Equal(2, warn.Length);
        Assert.Single(warn, w => w.StartsWith("W=MD:", StringComparison.Ordinal));
        Assert.Single(warn, w => w.StartsWith("W=NP:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_у_чернетці_ретайпить_String_в_Decimal_а_повтор_дає_нуль_змін()
    {
        var output = Run("draft");
        var runs = output.Split("RUN=2", StringSplitOptions.None);

        Assert.Equal(["RETYPED=BZ", "RETYPED=PC"], Lines(runs[0], "RETYPED="));
        Assert.Contains("LOG=    String->Decimal: S/T/BZ", runs[0], StringComparison.Ordinal);
        Assert.Matches(@"LOG=.*String->Decimal.*: 2\s", runs[0]);
        Assert.Contains("LOG=  WARN тип колонки: S/T/MD:", runs[0], StringComparison.Ordinal);
        Assert.Empty(Lines(output, "TEXT="));   // текстовий ретайп і скидання precision мостів не задіяні

        Assert.Empty(Lines(runs[1], "RETYPED="));
        Assert.Matches(@"LOG=.*String->Decimal.*: 0\s", runs[1]);

        // рядок - як у свіжої Decimal-колонки моделі: 34/16, одиниця моделі, DisplayFormat скинуто (його пише презентація)
        Assert.Contains("STATE=BZ:2:34:16:u:f\n", output.Replace("\r", string.Empty), StringComparison.Ordinal);
        Assert.Contains("STATE=PC:2:34:16:u42:f\n", output.Replace("\r", string.Empty), StringComparison.Ordinal);
        Assert.Contains("STATE=MD:0:::u:f@", output, StringComparison.Ordinal);
        Assert.Contains("STATE=TX:0:::u:f@", output, StringComparison.Ordinal);
        Assert.Contains("STATE=IN:1:::u:f@", output, StringComparison.Ordinal);
        Assert.Contains("STATE=NP:0:::u:f@", output, StringComparison.Ordinal);
        Assert.Contains("STATE=BRG:6:::u:f@", output, StringComparison.Ordinal);   // міст не чіпається
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_не_ретайпить_у_опублікованій_версії_і_колонку_зі_значеннями()
    {
        var published = Run("published");
        Assert.Empty(Lines(published, "RETYPED="));
        Assert.Contains("STATE=BZ:0:::u:f@", published, StringComparison.Ordinal);
        Assert.Contains("S/T/BZ: UPDATE String->Decimal не застосовано", published, StringComparison.Ordinal);

        var withValues = Run("values");
        Assert.Equal(["RETYPED=BZ"], Lines(withValues.Split("RUN=2", StringSplitOptions.None)[0], "RETYPED="));
        Assert.Contains("STATE=PC:0:::u:f@", withValues, StringComparison.Ordinal);   // PC має значення комірок
        Assert.Contains("S/T/PC: UPDATE String->Decimal не застосовано", withValues, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void SQL_гарди_String_в_Decimal_у_Fill_StructureSql()
    {
        var build = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));
        var fill = build[build.IndexOf("function Fill-StructureSql", StringComparison.Ordinal)..];
        fill = fill[..fill.IndexOf("\nfunction ", StringComparison.Ordinal)];

        Assert.Contains("Get-LandDecimalRetypePlan @($tb.columns) $exN", fill, StringComparison.Ordinal);
        Assert.Contains(
            "UPDATE c SET DataType=2, Precision=@p, Scale=@s, DisplayFormat=NULL, UnitId=@u FROM cfg.ColumnDef c ",
            fill,
            StringComparison.Ordinal);
        Assert.Contains(
            "v.Status=0 AND c.DataType=0 AND c.IsMonthColumn=0 AND NOT EXISTS (SELECT 1 FROM doc.CellValue cv WHERE cv.ColumnDefId=c.Id)",
            fill,
            StringComparison.Ordinal);

        // ретайп - до вставки нових колонок і до презентації (DisplayFormat за Resolve-LandDisplayFormat пишеться ПІСЛЯ)
        Assert.True(
            fill.IndexOf("Get-LandDecimalRetypePlan", StringComparison.Ordinal)
            < fill.IndexOf("презентація колонок (L1)", StringComparison.Ordinal));
    }
}
