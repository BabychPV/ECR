using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-423 (C11_10, LAND-5094): (1) місячні колонки <c>OperatingTimeReleaseOnColdVentYesNo_Mnn</c> у T9_04/T9_07 - String
/// (години АБО <c>No - Нет</c>/<c>Yes - Да</c>), мости Formula над ними текстові (без precision), решта таблиць не змінена;
/// (2) завантажувач пише текст у String-колонку й рахує текст/число та NAE type3 у T9_04/T9_07; (3) етап
/// <c>-Stage NaeType</c> обгортає type1/2/3 за NAE, як старий код (ECW_C11_10.cs:180-184, :305-311). Значень прода тут немає.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): повернути Decimal у <c>special()</c> (gen_ecr_model.py) і перегенерувати модель -
/// <see cref="Колонки_T9_04_T9_07_String_мости_текстові_решта_таблиць_не_змінена"/> червоніє; прибрати гілку лічильника
/// <c>MixedText</c> - <see cref="Завантажувач_пише_текст_у_String_колонку_і_рахує_текст_число_type3"/>; прибрати
/// <c>-cnotcontains</c>-аналог (повтор обгортки) - <see cref="Обгортка_за_NAE_ідемпотентна_додає_аргумент_і_рахує_обидва_написання"/>.
/// </remarks>
public sealed class LandOperatingTimeStringTests
{
    private const string No = "No - Нет";
    private const string Yes = "Yes - Да";

    private static string Run(string body, params string[] libs)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-optime-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            var sb = new StringBuilder("$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n");
            foreach (var lib in libs)
            {
                sb.Append(". '").Append(LandRowKeyTests.LandFile(lib).Replace("'", "''")).Append("'\n");
            }

            sb.Append(body);
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var (exit, output) = LandRowKeyTests.RunPowerShell(script);
            Assert.True(exit == 0, output);
            return output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') || l.StartsWith('['));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static JsonElement Model()
    {
        var path = Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static JsonElement Column(JsonElement model, string table, string code)
    {
        foreach (var sheet in model.GetProperty("sheets").EnumerateArray())
        {
            foreach (var t in sheet.GetProperty("tables").EnumerateArray())
            {
                if (t.GetProperty("code").GetString() != table)
                {
                    continue;
                }

                foreach (var c in t.GetProperty("columns").EnumerateArray())
                {
                    if (c.GetProperty("code").GetString() == code)
                    {
                        return c;
                    }
                }
            }
        }

        throw new InvalidOperationException($"{table}.{code} немає в моделі");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонки_T9_04_T9_07_String_мости_текстові_решта_таблиць_не_змінена()
    {
        var model = Model();
        foreach (var table in new[] { "T9_04", "T9_07" })
        {
            for (var m = 1; m <= 12; m++)
            {
                var c = Column(model, table, $"OperatingTimeReleaseOnColdVentYesNo_M{m:00}");
                Assert.Equal("String", c.GetProperty("dataType").GetString());
            }

            var bridge = Column(model, table, "Land_Measure_OperatingTime");
            Assert.Equal("Formula", bridge.GetProperty("dataType").GetString());
            Assert.Equal(JsonValueKind.Null, bridge.GetProperty("precision").ValueKind);   // текстовий міст (textResult)
            Assert.Equal("h", bridge.GetProperty("unit").GetString());                     // одиниця лишається: міграція без Guarded UnitId
        }

        // K1: суфіксовані мости T9_04 копіюють ту саму колонку - теж текстові
        foreach (var sfx in new[] { "ECW_C11_01", "ECW_C11_02" })
        {
            var b = Column(model, "T9_04", $"Land_Measure_OperatingTime__{sfx}");
            Assert.Equal(JsonValueKind.Null, b.GetProperty("precision").ValueKind);
            Assert.Contains("OperatingTimeReleaseOnColdVentYesNo_M01", b.GetProperty("expression").GetString(), StringComparison.Ordinal);
        }

        // інші таблиці й ReleaseOnColdVent (Sm3) лишаються числовими
        Assert.Equal("Decimal", Column(model, "T9_01", "OperatingTime_M01").GetProperty("dataType").GetString());
        Assert.Equal("Decimal", Column(model, "T9a_24", "ReleaseOnColdVent20Dgr1atm_M01").GetProperty("dataType").GetString());
        Assert.Equal("Lookup", Column(model, "T10_01", "ReleaseOnColdVentYesNo_M01").GetProperty("dataType").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Завантажувач_пише_текст_у_String_колонку_і_рахує_текст_число_type3()
    {
        var json = Run(
            $$"""
            function Col($code, $type) { @{ code = $code; dataType = $type; readOnly = $false; expr = $null; reg = $null } }
            $cols = @{ 'OperatingTimeReleaseOnColdVentYesNo_M01' = (Col 'OperatingTimeReleaseOnColdVentYesNo_M01' 'String') }
            $alias = @{ 'Land_Measure_OperatingTime' = @{ Column = 'OperatingTimeReleaseOnColdVentYesNo_M01'; MonthColumns = @() } }
            $src = @(@{ Land_Measure_OperatingTime = '{{No}}'; Land_NAE = '0647 (001-002)' },
                     @{ Land_Measure_OperatingTime = '740.900000'; Land_NAE = '0900(001)' },
                     @{ Land_Measure_OperatingTime = '{{Yes}}'; Land_NAE = '0221' })
            $p4 = New-LandRowsTablePlan -DocNumber 6 -TableCode 'T9_04' -MonthNo 1 -SourceRows $src -ColumnMap $cols -TableAliases $alias
            $p2 = New-LandRowsTablePlan -DocNumber 6 -TableCode 'T9_02' -MonthNo 1 -SourceRows $src -ColumnMap $cols -TableAliases $alias
            [ordered]@{
              rows = $p4.Rows.Count
              v0 = $p4.Rows[0].cells[0].value; v1 = $p4.Rows[1].cells[0].value; v2 = $p4.Rows[2].cells[0].value
              text = $p4.MixedText; num = $p4.MixedNumber; t3 = $p4.Type3InMixed; t3other = $p2.Type3InMixed
            } | ConvertTo-Json -Compress
            """,
            "LandRows.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal(3, r.GetProperty("rows").GetInt32());
        Assert.Equal(No, r.GetProperty("v0").GetString());           // текст НЕ губиться (Decimal-завантажувач його пропускав)
        Assert.Equal("740.900000", r.GetProperty("v1").GetString());
        Assert.Equal(Yes, r.GetProperty("v2").GetString());
        Assert.Equal(2, r.GetProperty("text").GetInt32());
        Assert.Equal(1, r.GetProperty("num").GetInt32());
        Assert.Equal(1, r.GetProperty("t3").GetInt32());              // NAE 0221 (type3) у T9_04
        Assert.Equal(0, r.GetProperty("t3other").GetInt32());         // T9_02 - не змішана колонка
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_за_NAE_ідемпотентна_додає_аргумент_і_рахує_обидва_написання()
    {
        var json = Run(
            """
            $g = Get-LandNaeTypeGroups
            $t2 = @($g['ECW_C11_10']['type2'])
            $r1 = Convert-LandNaeTypeFormula -Expression '1 + @X' -ArgumentsCsv '@X;@Land_Measure_OperatingTime?;' -Naes $t2
            $r2 = Convert-LandNaeTypeFormula -Expression $r1.Expression -ArgumentsCsv $r1.ArgumentsCsv -Naes $t2
            $r3 = Convert-LandNaeTypeFormula -Expression '1' -ArgumentsCsv $null -Naes $t2
            $r4 = Convert-LandNaeTypeFormula -Expression '' -ArgumentsCsv '@X;' -Naes $t2
            $r5 = Convert-LandNaeTypeFormula -Expression '1' -ArgumentsCsv '@Land_NAE;' -Naes $t2
            [ordered]@{
              e1 = $r1.Expression; a1 = $r1.ArgumentsCsv; c2 = $r2.Changed; e2 = $r2.Expression
              c3 = $r3.Changed; a3 = $r3.ArgumentsCsv; c4 = $r4.Changed; a5 = $r5.ArgumentsCsv
              ty = @((Get-LandNaeTypeOfFormula 'ECW_EC_gsec_XXX_type2'), (Get-LandNaeTypeOfFormula 'ECW_EC_tons_333_type3'),
                     (Get-LandNaeTypeOfFormula 'ECW_EC_tons_XXX'), (Get-LandNaeTypeOfFormula 'ECW_EC_tons_XXX_type1_1'))
              n1 = ConvertTo-LandNaeNormalized ' 0647 (001-002)'
              i2 = Test-LandNaeInType '0647 (001-002)' 'ECW_C11_10' 'type2'
              i1 = Test-LandNaeInType '0647' 'ECW_C11_10' 'type1'
              i3 = Test-LandNaeInType '0221' 'ECW_C11_10' 'type3'
              none = Test-LandNaeInType '0900(001)' 'ECW_C11_10' 'type2'
              types = @($g['ECW_C11_10'].Keys)
            } | ConvertTo-Json -Compress -Depth 5
            """,
            "LandNaeType.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var e1 = r.GetProperty("e1").GetString()!;
        Assert.StartsWith("if(in(@Land_NAE, '0647', '0647 (001-002)', '0648', '0648 (001-002)'", e1, StringComparison.Ordinal);
        Assert.Contains("'0884', '0885'", e1, StringComparison.Ordinal);
        Assert.EndsWith("), 1 + @X, NULL)", e1, StringComparison.Ordinal);
        Assert.Equal("@X;@Land_Measure_OperatingTime?;@Land_NAE;", r.GetProperty("a1").GetString());

        // повтор - 0 змін
        Assert.False(r.GetProperty("c2").GetBoolean());
        Assert.Equal(e1, r.GetProperty("e2").GetString());

        // список $null лишається $null; порожній вираз не чіпається; наявний @Land_NAE не дублюється
        Assert.Equal(JsonValueKind.Null, r.GetProperty("a3").ValueKind);
        Assert.False(r.GetProperty("c4").GetBoolean());
        Assert.Equal("@Land_NAE;", r.GetProperty("a5").GetString());

        Assert.Equal(
            ["type2", "type3", "", ""],
            r.GetProperty("ty").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? string.Empty : e.GetString()!).ToArray());
        Assert.Equal("0647(001-002)".Split('(')[0], r.GetProperty("n1").GetString());
        Assert.True(r.GetProperty("i2").GetBoolean());
        Assert.False(r.GetProperty("i1").GetBoolean());
        Assert.True(r.GetProperty("i3").GetBoolean());
        Assert.False(r.GetProperty("none").GetBoolean());
        Assert.Equal(["type1", "type2", "type3"], r.GetProperty("types").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_NaeType_лише_явно_чернетки_перед_OptionalArguments_і_правило_у_рішеннях()
    {
        var build = File.ReadAllText(LandRowKeyTests.LandFile("Build-LandTemplate.ps1"));
        Assert.Matches(@"if \(\$Stage -contains 'NaeType'\) \{ Stage-NaeType \}", build);
        Assert.DoesNotMatch(@"Want 'NaeType'", build);
        Assert.Matches(@"ValidateSet\([^)]*'NaeType'", build);
        Assert.True(
            build.IndexOf("Stage-NaeType }", StringComparison.Ordinal) < build.IndexOf("Stage-OptionalArguments }", StringComparison.Ordinal));
        Assert.Contains("опублікована - не чіпаю", build[build.IndexOf("function Stage-NaeType", StringComparison.Ordinal)..], StringComparison.Ordinal);

        var decisions = File.ReadAllText(Path.Combine(SourceTree.Root, "docs", "tz", "10-decisions.md"));
        Assert.Contains("D-423", decisions, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сторож LAND-POST-K3 (2026-10-15): методологія з суфіксним мостом над String-колонкою OperatingTime* (C11_01/C11_02)
    /// не може бути Strict: Text x Number = #VALUE, а Legacy розбирає числовий рядок у double до формули. Режим живе в БД
    /// стенда, тому перевірка - у Verify-LandTemplate.ps1 (живий стенд), а тут - модель + чиста логіка + наявність виклику.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Суфіксний_міст_над_String_колонкою_OperatingTime_не_в_Strict_сторож_у_Verify()
    {
        var modelPath = Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        var json = Run(
            $$"""
            $mdl = Get-Content -LiteralPath '{{modelPath.Replace("'", "''")}}' -Raw -Encoding UTF8 | ConvertFrom-Json
            $br = @(Get-LandStringBridgeSuffixMethodologies -Model $mdl)
            function V($c, $m) { [pscustomobject]@{ Code = $c; NumericMode = $m } }
            $legacy = @(Get-LandStringBridgeStrictMethodologies -Methodologies $br -Versions @((V 'ECW_C11_01' 'Legacy'), (V 'ECW_C11_02' 'Legacy'), (V 'ECW_C11_10' 'Strict')))
            $one = @(Get-LandStringBridgeStrictMethodologies -Methodologies $br -Versions @((V 'ECW_C11_01' 'Strict'), (V 'ECW_C11_02' 'Legacy')))
            $both = @(Get-LandStringBridgeStrictMethodologies -Methodologies $br -Versions @((V 'ECW_C11_01' 'Strict'), (V 'ECW_C11_02' 'Strict')))
            [ordered]@{ bridge = @($br); legacy = @($legacy); one = @($one); both = @($both) } | ConvertTo-Json -Compress -Depth 4
            """,
            "LandArgumentSuffix.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string[] Arr(string n) => r.GetProperty(n).EnumerateArray().Select(e => e.GetString()!).ToArray();

        // мости над String-колонкою - рівно C11_01/C11_02; C11_11_02 (Land_Measure_SubstanceConsumption) і C11_10 не входять
        Assert.Equal(["ECW_C11_01", "ECW_C11_02"], Arr("bridge"));
        Assert.Empty(Arr("legacy"));                     // Legacy - ок, C11_10 Strict поза переліком мостів
        Assert.Equal(["ECW_C11_01"], Arr("one"));
        Assert.Equal(["ECW_C11_01", "ECW_C11_02"], Arr("both"));

        var verify = File.ReadAllText(LandRowKeyTests.LandFile("Verify-LandTemplate.ps1"));
        Assert.Contains("Get-LandStringBridgeStrictMethodologies", verify, StringComparison.Ordinal);
        Assert.Matches(@"\$bad \+= ""методології з суфіксним мостом над String-колонкою в NumericMode=Strict", verify);
    }
}
