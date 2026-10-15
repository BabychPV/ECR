using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Завантажувач рядків повного набору Land (tools/land/Load-LandRows.ps1 + LandRows.Lib.ps1, LAND-5094): СИНТЕТИЧНІ дані
/// без прода. Сторожі: (а) мапінг (місячні колонки моделі, FB_*No, ReleaseColdVent, NumberOperations, T6_12 K*/B1, IDCodeMPEProject,
/// FuelType), (б) за замовчуванням немає капу, (в) rowKey з кодом таблиці й унікальний між таблицями, (г) відмови охорони
/// стенда, (д) NO_TABLE дає код виходу 1, (е) вихід без значень, (є) лічильник source-without-column.
/// </summary>
public sealed class LandRowLoaderTests
{
    private static string Probe(string body, string lib = "LandRows.Lib.ps1")
    {
        var dir = Directory.CreateTempSubdirectory("ecr-rows-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            File.WriteAllText(
                script,
                "$ErrorActionPreference = 'Stop'\n" +
                $". '{LandRowKeyTests.LandFile(lib).Replace("'", "''")}'\n" +
                """
                function Col($code, $type = 'Decimal', [bool]$ro = $false) { @{ code = $code; dataType = $type; readOnly = $ro; expr = $null; reg = $null } }
                """ + "\n" + body);
            var (exit, output) = LandRowKeyTests.RunPowerShell(script);
            Assert.True(exit == 0, output);
            return output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') || l.StartsWith('['));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Мапінг_покриває_місячні_колонки_FB_No_ReleaseColdVent_NumberOperations_і_T6_12()
    {
        var json = Probe("""
            $cols = @{}
            foreach ($c in 'SubstanceConsumption_M03', 'FB_PumpNo', 'ReleaseOnColdVentYesNo_M03', 'NumberOfOperationsN_M03', 'IDCodeMPEProject',
                           'K1ShareOfDustFractionInTheMaterial', 'B1FactorTakingIntoAccountLoadingHeig', 'DustTrappingEfficiency', 'FuelType') { $cols[$c] = Col $c }
            $cols['Land_Agent_Fraction'] = Col 'Land_Agent_Fraction' 'Decimal' $true      # formula in ECR: never a target
            $cols['Land_TypeFuel'] = Col 'Land_TypeFuel' 'Lookup' $true
            $alias = @{ 'Land_Measure_SubstanceConsumptionKgH' = @{ Column = $null; MonthColumns = @(1..12 | ForEach-Object { 'SubstanceConsumption_M{0:00}' -f $_ }) }
                        'Land_Measure_Ghost' = @{ Column = 'NoSuchColumn'; MonthColumns = @() } }
            function T($p) { $r = Resolve-LandRowsColumn -ProdColumn $p -MonthNo 3 -ColumnMap $cols -TableAliases $alias
                            [ordered]@{ def = $(if ($r.Def) { $r.Def.code } else { $null }); missing = $r.MissingTarget } }
            [ordered]@{
              month = T 'Land_Measure_SubstanceConsumptionKgH'
              fb = T 'No_Pump'
              cold = T 'Land_Measure_ReleaseColdVent'
              ops = T 'Land_Measure_NumberOperations'
              nae = T 'Land_NAE'
              k1 = T 'Land_Agent_Fraction'
              b1 = T 'Land_Agent_AccountLoading'
              trap = T 'Land_TrappingEfficienty'
              fuel = T 'Land_TypeFuel'
              ghost = T 'Land_Measure_Ghost'
              none = T 'Land_Unknown'
            } | ConvertTo-Json -Depth 4 -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Def(string k) => r.GetProperty(k).GetProperty("def").GetString()!;

        Assert.Equal("SubstanceConsumption_M03", Def("month"));
        Assert.Equal("FB_PumpNo", Def("fb"));
        Assert.Equal("ReleaseOnColdVentYesNo_M03", Def("cold"));
        Assert.Equal("NumberOfOperationsN_M03", Def("ops"));
        Assert.Equal("IDCodeMPEProject", Def("nae"));                 // T6_12: не IDCodeNAEProject
        Assert.Equal("K1ShareOfDustFractionInTheMaterial", Def("k1"));   // формульна Land_Agent_* не ціль
        Assert.Equal("B1FactorTakingIntoAccountLoadingHeig", Def("b1"));
        Assert.Equal("DustTrappingEfficiency", Def("trap"));
        Assert.Equal("FuelType", Def("fuel"));                        // Land_TypeFuel формульна: alias на FuelType
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ghost").GetProperty("def").ValueKind);
        Assert.Equal("NoSuchColumn", r.GetProperty("ghost").GetProperty("missing").GetString());   // відсутня колонка - знахідка
        Assert.Equal(JsonValueKind.Null, r.GetProperty("none").GetProperty("def").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Без_капу_за_замовчуванням_ключ_з_кодом_таблиці_унікальний_між_таблицями()
    {
        var json = Probe("""
            $cols = @{ 'Amount' = (Col 'Amount'); 'Density' = (Col 'Density') }
            $src = @(1..788 | ForEach-Object { @{ Amount = "$_.5" } })
            $none = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T3_00' -MonthNo 1 -SourceRows $src -ColumnMap $cols
            $capped = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T3_00' -MonthNo 1 -SourceRows $src -ColumnMap $cols -RowLimit 498
            $other = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T4_01' -MonthNo 1 -SourceRows @($src[0]) -ColumnMap $cols
            $legacy = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T4_01' -MonthNo 1 -SourceRows @($src[0]) -ColumnMap $cols -KeyFormat Legacy
            [ordered]@{
              rows = $none.Rows.Count; capped = $none.Capped
              cRows = $capped.Rows.Count; cCapped = $capped.Capped
              first = $none.Rows[0].rowKey; last = $none.Rows[787].rowKey; other = $other.Rows[0].rowKey; legacy = $legacy.Rows[0].rowKey
              absent = @(Get-LandRowsSourceWithoutColumn -ColumnMap $cols -FilledColumns @('Amount'))
            } | ConvertTo-Json -Depth 4 -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal(788, r.GetProperty("rows").GetInt32());          // 498 з 788 більше не зрізаються
        Assert.Equal(0, r.GetProperty("capped").GetInt32());
        Assert.Equal(498, r.GetProperty("cRows").GetInt32());         // явний кап лишається можливим і лічиться
        Assert.Equal(290, r.GetProperty("cCapped").GetInt32());
        Assert.Equal("p7-T3_00-0001", r.GetProperty("first").GetString());
        Assert.Equal("p7-T3_00-0788", r.GetProperty("last").GetString());
        Assert.Equal("p7-T4_01-0001", r.GetProperty("other").GetString());   // той самий порядковий номер, інша таблиця
        Assert.Equal("p7-0001", r.GetProperty("legacy").GetString());
        Assert.Equal(["Density"], r.GetProperty("absent").EnumerateArray().Select(e => e.GetString()!).ToArray());   // source-without-column
    }

    // ---- script ------------------------------------------------------------------------------------------------

    private static (string Dir, string Sample) Sample(string prodTable, string column, string sentinel)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-rows-script-").FullName;
        var sample = new
        {
            doc = "LAND-PROD-7",
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    [prodTable] = new { columns = new[] { column }, rows = new[] { new Dictionary<string, object> { [column] = sentinel } } },
                },
            },
        };
        File.WriteAllText(Path.Combine(dir, "LAND-PROD-7.json"), JsonSerializer.Serialize(sample));
        return (dir, Path.Combine(dir, "LAND-PROD-7.json"));
    }

    private static string FirstStringColumn(string tableCode)
    {
        using var model = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var table = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .First(t => t.GetProperty("code").GetString() == tableCode);
        return table.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("dataType").GetString() == "String"
                && !(c.TryGetProperty("readOnly", out var ro) && ro.GetBoolean())
                && !c.TryGetProperty("expression", out _))
            .GetProperty("code").GetString()!;
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_у_режимі_плану_будує_рядки_без_значень_у_виході_і_охорона_стенда_відмовляє()
    {
        const string sentinel = "SENTINEL-4242";
        var column = FirstStringColumn("T3_00");
        var (dir, _) = Sample("AF_Air_Land_Stationary_Equipment", column, sentinel);
        try
        {
            var common = new[]
            {
                LandRowKeyTests.LandFile("Load-LandRows.ps1"), "-SourcePath", dir, "-DocMap", "{\"LAND-PROD-7\":12}",
                "-BaseUrl", "http://localhost:5094",
            };

            var (exit, output) = LandRowKeyTests.RunPowerShell([.. common, "-ConfirmStand", "localhost:5094"]);
            Assert.True(exit == 0, output);
            Assert.Contains("S03_STATIONARY_EQUIPMENT|T3_00", output, StringComparison.Ordinal);
            Assert.Matches(@"rows=1 cells=1 capped=0", output);
            Assert.Contains("rowKeyFormat Table", output, StringComparison.Ordinal);
            Assert.DoesNotContain(sentinel, output, StringComparison.Ordinal);

            var (e2, o2) = LandRowKeyTests.RunPowerShell(common);                       // без -ConfirmStand
            Assert.NotEqual(0, e2);
            Assert.DoesNotContain("PREVIEW", o2, StringComparison.Ordinal);

            var (e3, o3) = LandRowKeyTests.RunPowerShell([.. common, "-ConfirmStand", "localhost:5094", "-Execute"]);   // без -SqlConnection
            Assert.NotEqual(0, e3);
            Assert.Contains("SqlConnection", o3, StringComparison.Ordinal);
            Assert.DoesNotContain("PREVIEW", o3, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Невідома_таблиця_прода_дає_NO_TABLE_і_код_виходу_1()
    {
        var (dir, _) = Sample("AF_Air_Land_Bogus_Table", "Land_Whatever", "SENTINEL-1");
        try
        {
            var (exit, output) = LandRowKeyTests.RunPowerShell(
                LandRowKeyTests.LandFile("Load-LandRows.ps1"), "-SourcePath", dir, "-DocMap", "{\"LAND-PROD-7\":12}",
                "-BaseUrl", "http://localhost:5094", "-ConfirmStand", "localhost:5094");
            Assert.Equal(1, exit);
            Assert.Contains("NO_TABLE", output, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL-1", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void NAME_MISMATCH_блокує_документ_якщо_явно_не_дозволено()
    {
        var json = Probe("""
            [ordered]@{
              noDoc = Get-LandRowsDocBlock -NameState 'NO_DOC'
              mismatch = Get-LandRowsDocBlock -NameState 'NAME_MISMATCH'
              allowed = Get-LandRowsDocBlock -NameState 'NAME_MISMATCH' -AllowNameMismatch
              ok = Get-LandRowsDocBlock -NameState 'OK'
              empty = Get-LandRowsDocBlock -NameState ''
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("NO_DOC", r.GetProperty("noDoc").GetString());
        Assert.Equal("NAME_MISMATCH", r.GetProperty("mismatch").GetString());
        Assert.Equal(string.Empty, r.GetProperty("allowed").GetString());
        Assert.Equal(string.Empty, r.GetProperty("ok").GetString());
        Assert.Equal(string.Empty, r.GetProperty("empty").GetString());

        // Скрипт справді користується цим рішенням і має явну опцію.
        var script = File.ReadAllText(LandRowKeyTests.LandFile("Load-LandRows.ps1"));
        Assert.Contains("Get-LandRowsDocBlock", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$AllowNameMismatch", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_з_таблицею_виграє_навіть_коли_рядка_немає_у_Loaded_і_кандидатів_кілька()
    {
        var json = Probe("""
            $st = New-LandParityState
            $st.MethTables['ECW_C05_02'] = @('T3_00', 'T4_01')
            # Loaded is EMPTY: the legacy fallback would attribute the result to BOTH candidate tables.
            Add-LandParityEcrResult $st 'LAND-PROD-7' 202601 'ECW_C05_02' 'p7-T3_00-0001' 'ECW_EC_tons_301' 1.5
            [ordered]@{
              keys = @($st.Ecr.Keys | Sort-Object)
              multi = $st.MultiTable.Count
            } | ConvertTo-Json -Compress
            """, "LandParity.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(["LAND-PROD-7|202601|T3_00|p7-T3_00-0001"], keys);
        Assert.Equal(0, doc.RootElement.GetProperty("multi").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void SkipUnknownTables_і_IncludeTables_лічаться_а_за_замовчуванням_невідома_таблиця_блокує()
    {
        var (dir, _) = Sample("AF_Air_Land_Bogus_Table", "Land_Whatever", "SENTINEL-2");
        try
        {
            string[] common =
            [
                LandRowKeyTests.LandFile("Load-LandRows.ps1"), "-SourcePath", dir, "-DocMap", "{\"LAND-PROD-7\":12}",
                "-BaseUrl", "http://localhost:5094", "-ConfirmStand", "localhost:5094",
            ];
            var (e0, _) = LandRowKeyTests.RunPowerShell(common);
            Assert.Equal(1, e0);

            var (exit, output) = LandRowKeyTests.RunPowerShell([.. common, "-SkipUnknownTables"]);
            Assert.True(exit == 0, output);
            Assert.Contains("SKIPPED_UNKNOWN 1", output, StringComparison.Ordinal);
            Assert.Contains("WARN -SkipUnknownTables", output, StringComparison.Ordinal);

            var (e2, o2) = LandRowKeyTests.RunPowerShell([.. common, "-SkipUnknownTables", "-IncludeTables", "T9_04,T9_07"]);
            Assert.True(e2 == 0, o2);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }

        var column = FirstStringColumn("T3_00");
        var (dir2, _) = Sample("AF_Air_Land_Stationary_Equipment", column, "SENTINEL-3");
        try
        {
            var (exit, output) = LandRowKeyTests.RunPowerShell(
                LandRowKeyTests.LandFile("Load-LandRows.ps1"), "-SourcePath", dir2, "-DocMap", "{\"LAND-PROD-7\":12}",
                "-BaseUrl", "http://localhost:5094", "-ConfirmStand", "localhost:5094", "-IncludeTables", "T9_04");
            Assert.True(exit == 0, output);
            Assert.Contains("NOT_INCLUDED 1", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir2, recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Рядки_джерела_Dictionary_проходять_гілку_T9_04_без_MethodCountCouldNotFindBest()
    {
        // Windows PowerShell 5.1 віддає рядки як Dictionary<string,object>: у ній немає .Contains(key) (hotfix integ-land-loader-fix).
        var json = Probe("""
            # Той самий тип, що віддає JavaScriptSerializer (Dictionary`2), без Windows-only збірки System.Web.Extensions.
            $row = [System.Collections.Generic.Dictionary[string,object]]::new()
            $row['Land_No'] = '1'; $row['Land_NAE'] = 'N1'; $row['OperatingTime'] = '5'
            $rows = @($row)
            $cols = @{ Land_No = (Col 'Land_No' 'String'); OperatingTime = (Col 'OperatingTime' 'String') }
            $p = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T9_04' -MonthNo 1 -SourceRows $rows -ColumnMap $cols -TableAliases @{}
            [ordered]@{ rows = $p.Rows.Count; cells = $p.Cells } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("rows").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("cells").GetInt32());

        // Кожен споживач LandRows.Lib бачить функції LandInputCells.Lib: бібліотека підключає її сама, а скрипт - явно.
        Assert.Contains("LandInputCells.Lib.ps1", File.ReadAllText(LandRowKeyTests.LandFile("LandRows.Lib.ps1")), StringComparison.Ordinal);
        Assert.Contains("LandInputCells.Lib.ps1", File.ReadAllText(LandRowKeyTests.LandFile("Load-LandRows.ps1")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відхилення_422_агрегується_у_коди_ruleCode_columnCode_без_значень()
    {
        var json = Probe("""
            $body = '{"messageKey":"cell.rejected","cells":[{"ruleCode":"R1","columnCode":"C1","value":"SECRET-77","rowKey":"RK-1"},{"ruleCode":"R1","columnCode":"C1","value":"SECRET-88"},{"ruleCode":"R2","columnCode":"C2","value":"SECRET-99"}]}'
            $resp = [pscustomobject]@{ Status = 422; Json = ($body | ConvertFrom-Json); Text = $body }
            $bad = [pscustomobject]@{ Status = 422; Json = $null; Text = 'not json' }
            [ordered]@{ s = (Get-LandRowsRejectReasons -Response $resp); bad = (Get-LandRowsRejectReasons -Response $bad) } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("messageKey=cell.rejected, R1 C1 x2, R2 C2 x1", doc.RootElement.GetProperty("s").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("bad").GetString());
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RK-1", json, StringComparison.Ordinal);
        Assert.Contains("rejectReasons", File.ReadAllText(LandRowKeyTests.LandFile("Load-LandRows.ps1")), StringComparison.Ordinal);
    }

    // Дефект 15.10 (вікно 7): `{ ... }.GetNewClosure()` у Load-LandRows.ps1 ховав від блоку функції dot-sourced Lib
    // (New-LandLookupMap / Resolve-LandLookupId -> CommandNotFound). Closure у tools/land заборонено.
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипти_tools_land_не_використовують_GetNewClosure_бо_closure_не_бачить_функцій_Lib()
    {
        var dir = Path.Combine(SourceTree.Root, "tools", "land");
        var offenders = Directory.EnumerateFiles(dir, "*.ps1", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("GetNewClosure", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(dir, p))
            .ToArray();
        Assert.True(offenders.Length == 0, "GetNewClosure у: " + string.Join(", ", offenders));
    }
}