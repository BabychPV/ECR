using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Міст «шапка Region -> комірка Con_Region» (T5_27, ECW_C07_21). Діагноз LAND-POST-K10-DIAG-RESIDUE §(1): у PROD-5
/// немає жодної комірки Con_Region, рушій не підставляє DefaultValue, ECW_Category дає #ARG -> ECR-CALC-0422. Сторожі на
/// СИНТЕТИЧНИХ даних: (а) план пише Con_Region з тексту шапки в усі рядки T5_27 і лише в них; (б) порожня шапка - комірок
/// немає, дефолт не підставляється; (в) розбір поля шапки й запису довідника; (г) модель має колонку, а скрипти -
/// жодного літерала регіону.
/// </summary>
public sealed class LandConRegionBridgeTests
{
    private static JsonElement RunScript(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-conregion-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            File.WriteAllText(
                script,
                "$ErrorActionPreference = 'Stop'\n" +
                $". '{LandRowKeyTests.LandFile("LandInputCells.Lib.ps1").Replace("'", "''")}'\n" +
                body);
            var (exit, output) = LandRowKeyTests.RunPowerShell(script);
            Assert.True(exit == 0, output);
            var json = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') || l.StartsWith('['));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private const string Table5 = "AF_Air_Land_Other_Equipment_5_27";

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_пише_Con_Region_з_шапки_у_всі_рядки_T5_27_і_лише_їх()
    {
        var root = RunScript($$"""
            $doc = @{ periods = @{ '2026-01' = @{
                '{{Table5}}' = @{ rows = @( @{ Land_Measure_IgnitionsNumber = 1 }, @{ Land_Measure_IgnitionsNumber = 0 } ) }
                'AF_Air_Land_Off_Utility_DI_10_03' = @{ rows = @( @{ Land_Measure_GTU_Month = 'x' } ) } } } }
            $plan = New-LandInputCellPlan -KindName ConRegion -DocumentName 'LAND-PROD-5' -Document $doc -ConRegionText 'Atyrau - Atyrau-RU'
            [ordered]@{
              keys = @($plan.Keys)
              rows = @($plan['LAND-PROD-5|202601|T5_27'] | ForEach-Object { [ordered]@{ key = $_.RowKey; col = $_.Cells[0].Col; text = $_.Cells[0].Text; kind = $_.Cells[0].Kind } })
            } | ConvertTo-Json -Depth 5 -Compress
            """);

        Assert.Equal(["LAND-PROD-5|202601|T5_27"], root.GetProperty("keys").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var rows = root.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var r in rows)
        {
            Assert.Equal("Con_Region", r.GetProperty("col").GetString());
            Assert.Equal("Atyrau - Atyrau-RU", r.GetProperty("text").GetString());
            Assert.Equal("ConRegion", r.GetProperty("kind").GetString());
        }
    }

    [Theory]
    [InlineData("''")]
    [InlineData("'   '")]
    [InlineData("'NULL'")]
    [InlineData("$null")]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порожня_шапка_не_дає_комірок_і_дефолт_не_підставляється(string headerText)
    {
        var root = RunScript($$"""
            $doc = @{ periods = @{ '2026-01' = @{ '{{Table5}}' = @{ rows = @( @{ Land_Measure_IgnitionsNumber = 5 } ) } } } }
            $plan = New-LandInputCellPlan -KindName ConRegion -DocumentName 'LAND-PROD-5' -Document $doc -ConRegionText {{headerText}}
            $cells = @($plan['LAND-PROD-5|202601|T5_27'] | ForEach-Object { $_.Cells } | Where-Object { $_ })
            [ordered]@{ cells = $cells.Count; text = (($cells | ForEach-Object { $_.Text }) -join ';') } | ConvertTo-Json -Compress
            """);

        Assert.Equal(0, root.GetProperty("cells").GetInt32());
        Assert.DoesNotContain("Mangistau", root.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Поле_шапки_і_запис_довідника_розбираються_без_вгадування()
    {
        var root = RunScript("""
            $hdr = '{"fields":[{"code":"Area","value":7},{"code":"Region","value":42,"lookupRegistryDefId":9,"isEmpty":false}]}' | ConvertFrom-Json
            $hdrEmpty = '{"fields":[{"code":"Region","value":null,"isEmpty":true}]}' | ConvertFrom-Json
            $hdrNone = '{"fields":[{"code":"Area","value":7}]}' | ConvertFrom-Json
            $ref = Get-LandHeaderLookupEntryRef -Header $hdr -FieldCode 'Region'
            $withName = '{"id":42,"code":"REG_002","Values":{"NAME":"Mangistau - Mangistau-RU"}}' | ConvertFrom-Json
            $noName = '{"id":42,"Values":{"OTHER":"x"}}' | ConvertFrom-Json
            $blank = '{"id":42,"Values":{"NAME":"  "}}' | ConvertFrom-Json
            [ordered]@{
              entry = $ref.EntryId; reg = $ref.RegistryDefId
              empty = ($null -eq (Get-LandHeaderLookupEntryRef -Header $hdrEmpty -FieldCode 'Region'))
              none = ($null -eq (Get-LandHeaderLookupEntryRef -Header $hdrNone -FieldCode 'Region'))
              name = (Get-LandConRegionText -Entry $withName)
              noName = ($null -eq (Get-LandConRegionText -Entry $noName))
              blank = ($null -eq (Get-LandConRegionText -Entry $blank))
              nullEntry = ($null -eq (Get-LandConRegionText -Entry $null))
            } | ConvertTo-Json -Compress
            """);

        Assert.Equal(42, root.GetProperty("entry").GetInt32());
        Assert.Equal(9, root.GetProperty("reg").GetInt32());
        Assert.True(root.GetProperty("empty").GetBoolean());
        Assert.True(root.GetProperty("none").GetBoolean());
        Assert.Equal("Mangistau - Mangistau-RU", root.GetProperty("name").GetString());
        Assert.True(root.GetProperty("noName").GetBoolean());
        Assert.True(root.GetProperty("blank").GetBoolean());
        Assert.True(root.GetProperty("nullEntry").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Міст_присутній_у_завантажувачі_модель_має_колонку_літерала_регіону_немає()
    {
        var root = RunScript($$"""
            $k = Get-LandInputCellKind -Name ConRegion
            [ordered]@{ map = $k.TableMap['{{Table5}}']; sheet = $k.SheetOf['T5_27']; column = $k.Column; header = $k.HeaderField } | ConvertTo-Json -Compress
            """);
        Assert.Equal("T5_27", root.GetProperty("map").GetString());
        Assert.Equal("S05_OTHER_EQUIPMENT", root.GetProperty("sheet").GetString());
        Assert.Equal("Con_Region", root.GetProperty("column").GetString());
        Assert.Equal("Region", root.GetProperty("header").GetString());

        var loader = File.ReadAllText(LandRowKeyTests.LandFile("Load-LandInputCells.ps1"));
        Assert.Contains("'ConRegion'", loader, StringComparison.Ordinal);
        Assert.Contains("Get-LandHeaderLookupEntryRef", loader, StringComparison.Ordinal);
        Assert.Contains("-ConRegionText", loader, StringComparison.Ordinal);

        // Дефолт регіону в завантажувачі й бібліотеці заборонений (рішення координатора 15.10).
        var lib = File.ReadAllText(LandRowKeyTests.LandFile("LandInputCells.Lib.ps1"));
        Assert.DoesNotContain("Mangistau", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("Mangistau", lib, StringComparison.Ordinal);

        var model = Path.Combine(SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(model));
        var found = false;
        foreach (var sheet in doc.RootElement.GetProperty("sheets").EnumerateArray())
        {
            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                if (table.GetProperty("code").GetString() != "T5_27")
                {
                    continue;
                }

                found = table.GetProperty("columns").EnumerateArray()
                    .Any(c => c.GetProperty("code").GetString() == "Con_Region" && c.GetProperty("dataType").GetString() == "String");
            }
        }

        Assert.True(found, "У моделі T5_27 немає String-колонки Con_Region: міст писав би в неіснуючу колонку.");
    }
}
