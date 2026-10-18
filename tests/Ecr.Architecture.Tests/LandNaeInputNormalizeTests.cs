using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RCN1: вхідна нормалізація Land_NAE при завантаженні рядків (<c>ConvertTo-LandNaeInputNormalized</c> у
/// <c>LandRows.Lib.ps1</c>, виклик у <c>New-LandRowsTablePlan</c> лише для колонки <c>Land_NAE</c>). Легасі нормалізує
/// Trim + Replace(" ", ""); D-435 <c>in()</c> порівнює точно, тож NBSP/таб/NFD у прод-даних дали б хибні промахи.
/// Значень прода тут немає. Усі нелатинські/невидимі символи - екранування \uXXXX.
/// </summary>
/// <remarks>
/// Мутації (лише цей клас): прибрати <c> </c> з класу символів у Lib - <see cref="Таблиця_кейсів_нормалізації"/>
/// червоніє; прибрати виклик у <c>New-LandRowsTablePlan</c> - <see cref="План_нормалізує_лише_Land_NAE_і_рахує_зміни"/>
/// та <see cref="Виклик_нормалізації_один_і_лише_для_Land_NAE"/>.
/// </remarks>
public sealed class LandNaeInputNormalizeTests
{
    private static string Run(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-naein-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            var sb = new StringBuilder("$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n");
            sb.Append(". '").Append(LandRowKeyTests.LandFile("LandRows.Lib.ps1").Replace("'", "''")).Append("'\n");
            sb.Append("function Col($code, $type = 'String') { @{ code = $code; dataType = $type; readOnly = $false; expr = $null; reg = $null } }\n");
            sb.Append("function U($s) { [regex]::Unescape($s) }\n");
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

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Таблиця_кейсів_нормалізації()
    {
        var json = Run("""
            $cases = @(
              @('0500 (003)', '0500(003)'),
              @('0500 (003)', '0500(003)'),
              @('0500\t(003)', '0500(003)'),
              @(' 6567 ', '6567'),
              @('6567', '6567'),
              @('  0500 (003)﻿', '0500(003)'),
              @('é', 'é'),
              @(' ab(003) ', 'ab(003)'),
              @('05\u200B00 (003)', '0500(003)'),
              @('\u200B6567\u200B', '6567')
            )
            $bad = @()
            foreach ($c in $cases) {
                $in = U $c[0]; $want = U $c[1]
                $got = ConvertTo-LandNaeInputNormalized -Nae $in
                if ($got -cne $want) { $bad += $c[0] }
                if ((ConvertTo-LandNaeInputNormalized -Nae $got) -cne $got) { $bad += ('idem ' + $c[0]) }
            }
            if ($null -ne (ConvertTo-LandNaeInputNormalized -Nae $null)) { $bad += 'null' }
            if ((ConvertTo-LandNaeInputNormalized -Nae '') -cne '') { $bad += 'empty' }
            [ordered]@{ cases = $cases.Count; bad = @($bad) } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(10, doc.RootElement.GetProperty("cases").GetInt32());
        Assert.True(doc.RootElement.GetProperty("bad").GetArrayLength() == 0, json);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_нормалізує_лише_Land_NAE_і_рахує_зміни()
    {
        var json = Run("""
            $mk = { param($nae) $r = [System.Collections.Generic.Dictionary[string,object]]::new(); $r['Land_No'] = ' 1 '; $r['Land_NAE'] = $nae; $r }
            $cols = @{ Land_No = (Col 'Land_No'); Land_NAE = (Col 'Land_NAE') }
            $rows = @((& $mk (U '0500 (003)')), (& $mk '6567'))
            $p = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T3_00' -MonthNo 1 -SourceRows $rows -ColumnMap $cols -TableAliases @{}
            $cellOf = { param($row, $code) @($row.cells | Where-Object { $_.columnCode -eq $code })[0].value }
            [ordered]@{ changed = $p.NaeNormalized; nae0 = (& $cellOf $p.Rows[0] 'Land_NAE'); nae1 = (& $cellOf $p.Rows[1] 'Land_NAE')
                        no0 = (& $cellOf $p.Rows[0] 'Land_No') } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("changed").GetInt32());
        Assert.Equal("0500(003)", doc.RootElement.GetProperty("nae0").GetString());
        Assert.Equal("6567", doc.RootElement.GetProperty("nae1").GetString());
        Assert.Equal(" 1 ", doc.RootElement.GetProperty("no0").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_не_рахує_null_і_число_як_зміну_та_ключі_рядків_не_залежать_від_NAE()
    {
        // null і число 6567 (не рядок) не змінюються нормалізацією - лічильник не росте; два різні сирі NAE, що
        // нормалізуються однаково ('0500 (003)' / '0500(003)'), дають РІЗНІ rowKey: ключ будується з порядкового seq, не з Land_NAE.
        var json = Run("""
            $mk = { param($nae) $r = [System.Collections.Generic.Dictionary[string,object]]::new(); $r['Land_No'] = '1'; $r['Land_NAE'] = $nae; $r }
            $cols = @{ Land_No = (Col 'Land_No'); Land_NAE = (Col 'Land_NAE') }
            $rows = @((& $mk $null), (& $mk 6567), (& $mk '6567'))
            $p = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T3_00' -MonthNo 1 -SourceRows $rows -ColumnMap $cols -TableAliases @{}
            $rows2 = @((& $mk (U '0500\u00A0(003)')), (& $mk '0500(003)'))
            $p2 = New-LandRowsTablePlan -DocNumber 7 -TableCode 'T3_00' -MonthNo 1 -SourceRows $rows2 -ColumnMap $cols -TableAliases @{}
            [ordered]@{ changed = $p.NaeNormalized; changed2 = $p2.NaeNormalized; keys = @($p2.Rows | ForEach-Object { $_.rowKey }) } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("changed").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("changed2").GetInt32());
        var keys = doc.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToArray();
        Assert.Equal(2, keys.Distinct().Count());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Test_LandNaeInType_знімає_NBSP_таб_і_U200B_усередині_як_вхідна_нормалізація()
    {
        var json = Run("""
            [ordered]@{
              nbsp = (Test-LandNaeInType -Nae (U '0647\u00A0(001-002)') -Methodology 'ECW_C11_10' -Type 'type2')
              tab = (Test-LandNaeInType -Nae (U '06\t47 (001-002)') -Methodology 'ECW_C11_10' -Type 'type2')
              zw = (Test-LandNaeInType -Nae (U '06\u200B47 (001-002)') -Methodology 'ECW_C11_10' -Type 'type2')
              n = (ConvertTo-LandNaeNormalized -Nae (U ' 05\u00A000\t(003)'))
            } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("nbsp").GetBoolean());
        Assert.True(r.GetProperty("tab").GetBoolean());
        Assert.True(r.GetProperty("zw").GetBoolean());
        Assert.Equal("0500", r.GetProperty("n").GetString());   // хвіст (003) відрізається як і раніше (C11_10)
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Виклик_нормалізації_один_і_лише_для_Land_NAE()
    {
        var lib = File.ReadAllText(LandRowKeyTests.LandFile("LandRows.Lib.ps1"));
        var calls = lib.Split('\n').Where(l => l.Contains("ConvertTo-LandNaeInputNormalized -Nae")).ToArray();
        var call = Assert.Single(calls);
        Assert.StartsWith("if ($col -eq 'Land_NAE')", call.TrimStart());

        var loader = File.ReadAllText(LandRowKeyTests.LandFile("Load-LandRows.ps1"));
        Assert.Contains("$tp.NaeNormalized", loader);
        Assert.Contains("naeNormalized $totNaeNorm", loader);
    }
}
