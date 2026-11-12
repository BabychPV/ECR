using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LAND-YEAR, зріз L4 (рішення людини 12.11, D-474 варіант A): етап <c>-Stage YearMonths</c> перебудовує модель Land
/// під річний аркуш (версія 1.0.17.0). Чиста логіка — <c>tools/land/LandYearMonths.Lib.ps1</c>; тут вона виконується
/// справжнім PowerShell на синтетичній фікстурі й на <c>land-template.model.json</c>.
/// Рішення людини: (1) колонки результатів — по виходу (Jan…Dec, потім наступний), у групах заголовків;
/// (2) річні підсумки — лише як у xlsm замовника (<c>land-year-totals.json</c>); (3) <c>T7b_00.ECW_EC_tons</c> — теж
/// місячний; (4) 123 колонки «(current period)» прибрано.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише локально, не комітяться): у <c>Get-LandYearMonthsTableClasses</c> не позначати Varying —
/// червоніють лічильники; у <c>Convert-LandYearMonthsModel</c> виводити колонки «місяць за місяцем» (зовнішній цикл
/// по місяцях) — червоніє порядок; у <c>Get-LandTotalsPlan</c> <c>Index = $idx</c> замість <c>$idx + 1</c> — червоніє
/// місце підсумку; Selector не прибирати (<c>$new.Add($c)</c>) — червоніє прибирання; у класифікації не пропускати місячні
/// колонки — червоніє повтор; у <c>Test-LandYearMonthsCodes</c> не порівнювати родину опублікованого коду — червоніє R-3;
/// у <c>Get-LandMonthBandPlan</c> не виключати колонки груп виходу з місячних блоків — червоніють смуги; у
/// <c>Stage-YearMonths</c> зняти гейт «разом з іншими етапами» — червоніє етап.</remarks>
/// <remarks>
/// Перевірено 12.11 (lane/integ-land-year-builder): кожна з восьми мутацій давала 1–5 червоних тестів класу.
/// </remarks>
public sealed class LandYearMonthsLibTests
{
    private const string Prelude = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $root = $args[0]
        $land = Join-Path (Join-Path $root 'tools') 'land'
        . (Join-Path $land 'LandMonthBands.Lib.ps1')
        . (Join-Path $land 'LandYearMonths.Lib.ps1')
        $months = (Read-LandMonthBandSource (Join-Path $land 'land-month-bands.json')).months
        function New-Col([string]$Code, [string]$Type, $Month = $null, [string]$Expr = $null) {
            $h = [ordered]@{ code = $Code; dataType = $Type; month = $Month; headerEn = "H $Code"; headerRu = ''; hidden = $false; readOnly = $false; excel = 'Z'; unit = 't' }
            if ($Expr) { $h.expression = $Expr }
            return $h
        }
        function New-Fixture {
            $a = @((New-Col 'Fuel' 'Lookup'))
            foreach ($m in 1..12) { $a += New-Col ('X_M{0:00}' -f $m) 'Decimal' $m }
            $sel = ((1..11 | ForEach-Object { '[Period].Sequence = {0} ? [X_M{0:00}] : ' -f $_ }) -join '') + '[X_M12]'
            $a += New-Col 'Sel' 'Formula' $null $sel
            $a += New-Col 'Dens' 'Formula' $null "REGFIELD(REGFIND('R', [Period].Year, [Period].Sequence, [Fuel]), 'V')"
            $a += New-Col 'Days' 'Formula' $null '[Period].Days'
            $a += New-Col 'Tr' 'Formula' $null '[Dens] * [Sel]'
            $a += New-Col 'DIY' 'Formula' $null '([Period].Year % 4 = 0) ? 366 : 365'
            $a += New-Col 'Out_A' 'Calculated'
            $a += New-Col 'Out_B' 'Calculated'
            $mn = @('January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December')
            $t7 = @(); for ($i = 0; $i -lt 12; $i++) { $t7 += New-Col $mn[$i] 'Decimal' ($i + 1) }
            $t7 += New-Col 'Q4' 'Formula' $null 'SUM([October], [November], [December])'
            $t7 += New-Col 'Note' 'String'
            $t9 = @(); for ($i = 0; $i -lt 12; $i++) { $t9 += New-Col $mn[$i] 'Decimal' ($i + 1) }
            $t9 += New-Col 'TOTAL' 'Formula' $null 'SUM([January], [December])'
            $model = @{ versionNumber = '1.0.4.0'; conditionalFormats = @(); sheets = @(
                    @{ code = 'S1'; tables = @(
                            @{ code = 'A'; layout = 'MonthsInColumns'; columns = $a; rules = @(); calc = @{ methodologies = @('M1'); outputs = @('Out_A', 'Out_B') } },
                            @{ code = 'B'; layout = 'Static'; columns = @((New-Col 'Code' 'String')); rules = @() }) },
                    @{ code = 'S7'; tables = @(@{ code = 'T7'; layout = 'MonthsInColumns'; columns = $t7; rules = @() }) },
                    @{ code = 'S9'; tables = @(@{ code = 'T9'; layout = 'MonthsInColumns'; columns = $t9; rules = @() }) }) }
            return ($model | ConvertTo-Json -Depth 20) | ConvertFrom-Json
        }
        function New-Totals([string]$T9Expr = 'SUM([January], [December])') {
            $x = [pscustomobject]@{ sheet = 'x'; cell = 'T2'; formula = '=SUM(D2:O2)' }
            return @(
                [pscustomobject]@{ sheet = 'S7'; table = 'T7'; code = 'Annual'; after = 'Q4'; headerEn = 'Annual'; expression = 'SUM([January], [December])'; xlsm = $x },
                [pscustomobject]@{ sheet = 'S9'; table = 'T9'; code = 'TOTAL'; after = 'December'; headerEn = 'TOTAL'; expression = $T9Expr; xlsm = $x })
        }
        function Get-Table($Model, [string]$Code) { return @($Model.sheets | ForEach-Object { $_.tables } | Where-Object { $_.code -eq $Code })[0] }
        function Codes($Table) { return (@($Table.columns) | ForEach-Object { [string]$_.code }) -join ',' }

        """;

    private static string M12(string code) => string.Join(",", Enumerable.Range(1, 12).Select(m => $"{code}_M{m:00}"));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Фікстура_колонки_результатів_по_виходу_формули_місяця_і_псевдоніми()
    {
        var output = Run("""
            $r = Convert-LandYearMonthsModel -Model (New-Fixture) -MonthNames $months -Totals (New-Totals)
            $a = Get-Table $r.Model 'A'
            Write-Host ('ERR=' + @($r.Errors).Count)
            Write-Host ('CODES=' + (Codes $a))
            $st = @($r.Tables | Where-Object { $_.Table -eq 'A' })[0]
            Write-Host ('STAT=' + $st.Added + '/' + $st.Removed + '/' + $st.Selectors + '/' + $st.Varying + '/' + $st.Calculated)
            $c = @($a.columns | Where-Object { $_.code -eq 'Out_A_M03' })[0]
            Write-Host ('OUTA3=' + $c.dataType + '|' + $c.month + '|' + $c.headerEn + '|' + $c.unit + '|' + $c.yearMonthsOf + '|' + $c.readOnly)
            foreach ($k in 'Dens_M03', 'Days_M02', 'Days_M04', 'Days_M12', 'Tr_M05') { Write-Host ("F_$k=" + @($a.columns | Where-Object { $_.code -eq $k })[0].expression) }
            Write-Host ('ALIAS_SEL=' + ((@($r.Aliases | Where-Object { $_.Alias -eq 'Sel' } | Sort-Object Month | ForEach-Object { "$($_.Month):$($_.Column)" })) -join ','))
            Write-Host ('ALIAS_N=' + @($r.Aliases).Count + '|' + @($r.Aliases | Where-Object { $_.Alias -eq 'Dens' -and $_.Month -eq 7 })[0].Column)
            Write-Host ('BIND=' + ((@($r.Bindings | Where-Object { $_.Output -eq 'Out_B' } | Sort-Object Month | ForEach-Object { $_.Column })) -join ','))
            Write-Host ('BINDCOLS=' + ((Get-LandResultColumnCodes -Table $a -Output 'Out_A') -join ',') + '|' + ((Get-LandResultColumnCodes -Table (Get-Table (New-Fixture) 'A') -Output 'Out_A') -join ','))
            $g = @($r.Groups | Where-Object { $_.Code -eq 'MB_O_Out_A' })[0]
            Write-Host ('GROUP=' + $g.En + '|' + (@($g.Columns) -join ','))
            Write-Host ('GROUPS=' + ((@($r.Groups | ForEach-Object { $_.Code })) -join ','))
            Write-Host ('SELECTORLEFT=' + @($r.Model.sheets | ForEach-Object { $_.tables } | ForEach-Object { $_.columns } | Where-Object { Test-LandYearMonthSelector $_.expression }).Count)
            Write-Host ('B=' + (Codes (Get-Table $r.Model 'B')))
            Write-Host ('VER=' + $r.Model.yearMonthsTargetVersion + '|' + $r.Model.versionNumber)
            """);

        Assert.Contains("ERR=0\n", output, StringComparison.Ordinal);
        // рішення 1: на місці колонки — її 12 місяців, вихід за виходом; рішення 4: Sel прибрано; DIY (лише .Year) лишається
        var expected = "Fuel," + M12("X") + "," + M12("Dens") + "," + M12("Days") + "," + M12("Tr") + ",DIY," + M12("Out_A") + "," + M12("Out_B");
        Assert.Contains("CODES=" + expected + "\n", output, StringComparison.Ordinal);
        // додано 5×12, прибрано 6 (Sel, Dens, Days, Tr, Out_A, Out_B); Tr — змінна транзитивно (читає Dens і Sel)
        Assert.Contains("STAT=60/6/1/3/2\n", output, StringComparison.Ordinal);
        Assert.Contains("OUTA3=Calculated|3|March|t|Out_A|True\n", output, StringComparison.Ordinal);
        Assert.Contains("F_Dens_M03=REGFIELD(REGFIND('R', [Period].Year, 3, [Fuel]), 'V')\n", output, StringComparison.Ordinal);
        Assert.Contains("F_Days_M02=(([Period].Year % 4 = 0) ? 29 : 28)\n", output, StringComparison.Ordinal);
        Assert.Contains("F_Days_M04=30\n", output, StringComparison.Ordinal);
        Assert.Contains("F_Days_M12=31\n", output, StringComparison.Ordinal);
        Assert.Contains("F_Tr_M05=[Dens_M05] * [X_M05]\n", output, StringComparison.Ordinal);
        Assert.Contains("ALIAS_SEL=" + string.Join(",", Enumerable.Range(1, 12).Select(m => $"{m}:X_M{m:00}")) + "\n", output, StringComparison.Ordinal);
        Assert.Contains("ALIAS_N=48|Dens_M07\n", output, StringComparison.Ordinal);
        Assert.Contains("BIND=" + M12("Out_B") + "\n", output, StringComparison.Ordinal);
        Assert.Contains("BINDCOLS=" + M12("Out_A") + "|Out_A\n", output, StringComparison.Ordinal);
        Assert.Contains("GROUP=H Out_A|" + M12("Out_A") + "\n", output, StringComparison.Ordinal);
        Assert.Contains("GROUPS=MB_O_Dens,MB_O_Days,MB_O_Tr,MB_O_Out_A,MB_O_Out_B\n", output, StringComparison.Ordinal);
        Assert.Contains("SELECTORLEFT=0\n", output, StringComparison.Ordinal);
        Assert.Contains("B=Code\n", output, StringComparison.Ordinal);
        Assert.Contains("VER=1.0.17.0|1.0.4.0\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Річні_підсумки_лише_як_у_xlsm_тим_самим_виразом_і_місцем()
    {
        var output = Run("""
            $r = Convert-LandYearMonthsModel -Model (New-Fixture) -MonthNames $months -Totals (New-Totals)
            Write-Host ('T7=' + (Codes (Get-Table $r.Model 'T7')))
            Write-Host ('T7EXPR=' + @((Get-Table $r.Model 'T7').columns | Where-Object { $_.code -eq 'Annual' })[0].expression)
            Write-Host ('T9=' + (Codes (Get-Table $r.Model 'T9')))
            Write-Host ('TOT=' + ((@($r.Tables | ForEach-Object { "$($_.Table):$($_.Totals)" })) -join ','))
            $bad = Convert-LandYearMonthsModel -Model (New-Fixture) -MonthNames $months -Totals (New-Totals 'SUM([January])')
            Write-Host ('MISMATCH=' + ((@($bad.Errors)) -join ';'))
            # справжній перелік: рівно два підсумки xlsm, обидва вже є в моделі на своєму місці
            $spec = [IO.File]::ReadAllText((Join-Path $land 'land-year-totals.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
            Write-Host ('SPEC=' + ((@($spec.totals | ForEach-Object { "$($_.sheet)/$($_.table).$($_.code)@$($_.after)=$($_.xlsm.formula)" })) -join ';'))
            """);

        // підсумок вставлено відразу після Q4 (як Annual після 4Q у xlsm), перед Note
        Assert.Contains("T7=January,February,March,April,May,June,July,August,September,October,November,December,Q4,Annual,Note\n", output, StringComparison.Ordinal);
        Assert.Contains("T7EXPR=SUM([January], [December])\n", output, StringComparison.Ordinal);
        // наявний підсумок з тим самим виразом — без змін
        Assert.Contains("T9=January,February,March,April,May,June,July,August,September,October,November,December,TOTAL\n", output, StringComparison.Ordinal);
        // жодного підсумку деінде: у таблиці A (результати) підсумків немає
        Assert.Contains("TOT=A:0,T7:1\n", output, StringComparison.Ordinal);
        Assert.Contains("MISMATCH=T9.TOTAL: підсумок xlsm", output, StringComparison.Ordinal);
        Assert.Contains(
            "SPEC=S07_0_WATER_CONSOLIDATION/T7_00.Annual@Q4==SUM(D2:O2);S09B_SULFUR/T9b_00.TOTAL@December==SUM(E10:P10)\n",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повтор_дає_нуль_змін_і_вхідна_модель_не_змінюється()
    {
        var output = Run("""
            $in = New-Fixture
            $before = Codes (Get-Table $in 'A')
            $r1 = Convert-LandYearMonthsModel -Model $in -MonthNames $months -Totals (New-Totals)
            Write-Host ('INPUT=' + ((Codes (Get-Table $in 'A')) -eq $before) + '|' + [bool](Get-Table $in 'A').PSObject.Properties['yearMonths'])
            $r2 = Convert-LandYearMonthsModel -Model $r1.Model -MonthNames $months -Totals (New-Totals)
            Write-Host ('FIRST=' + $r1.Changes)
            Write-Host ('SECOND=' + $r2.Changes + '|' + @($r2.Errors).Count + '|' + @($r2.Tables).Count)
            Write-Host ('SAME=' + ((Codes (Get-Table $r1.Model 'A')) -eq (Codes (Get-Table $r2.Model 'A'))) + '|' + ((Codes (Get-Table $r1.Model 'T7')) -eq (Codes (Get-Table $r2.Model 'T7'))))
            Write-Host ('KEPT=' + @($r2.Bindings).Count + '|' + @($r2.Aliases).Count + '|' + @($r2.Groups).Count)
            """);

        Assert.Contains("INPUT=True|False\n", output, StringComparison.Ordinal);
        // 60 додано + 6 прибрано + 1 підсумок + цільова версія
        Assert.Contains("FIRST=68\n", output, StringComparison.Ordinal);
        Assert.Contains("SECOND=0|0|0\n", output, StringComparison.Ordinal);
        Assert.Contains("SAME=True|True\n", output, StringComparison.Ordinal);
        // прив'язки, псевдоніми й групи першого прогону переходять у другий
        Assert.Contains("KEPT=24|48|5\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void R3_нові_коди_тієї_ж_родини_без_колізій_і_без_повтору_прибраних()
    {
        var output = Run("""
            $r = Convert-LandYearMonthsModel -Model (New-Fixture) -MonthNames $months -Totals (New-Totals)
            $types = @{ 'A|Out_A' = 'Calculated'; 'A|Out_B' = 'Calculated'; 'A|Dens' = 'Formula'; 'A|Days' = 'Formula'; 'A|Tr' = 'Formula' }
            $ok = Test-LandYearMonthsCodes -Model $r.Model -SourceTypes $types -Published @{ 'A|Out_A_M01' = 'Decimal'; 'A|Dens_M02' = 'Calculated' }
            Write-Host ('OK=' + @($ok).Count)
            $pub = Test-LandYearMonthsCodes -Model $r.Model -SourceTypes $types -Published @{ 'A|Out_A_M01' = 'String' }
            Write-Host ('PUB=' + ((@($pub)) -join ';'))
            $fam = Test-LandYearMonthsCodes -Model $r.Model -SourceTypes @{ 'A|Out_B' = 'Lookup' }
            Write-Host ('FAM=' + @($fam).Count)
            # наявний код Dens_M01 (не з цього етапу) — колізія, а не тиха підміна
            $f = New-Fixture
            $a = Get-Table $f 'A'
            $a.columns = @($a.columns) + @([pscustomobject]@{ code = 'Dens_M01'; dataType = 'Decimal'; month = 1 })
            $c = Convert-LandYearMonthsModel -Model $f -MonthNames $months -Totals (New-Totals)
            Write-Host ('COLLIDE=' + ((@($c.Errors)) -join ';'))
            Write-Host ('FAMILY=' + (Get-LandTypeFamily 'Calculated') + '|' + (Get-LandTypeFamily 'Formula') + '|' + (Get-LandTypeFamily 'String'))
            """);

        Assert.Contains("OK=0\n", output, StringComparison.Ordinal);
        Assert.Contains("PUB=A.Out_A_M01: опублікований код іншої родини (String -> Calculated)\n", output, StringComparison.Ordinal);
        Assert.Contains("FAM=12\n", output, StringComparison.Ordinal);
        Assert.Contains("COLLIDE=A.Dens_M01: код уже зайнятий колонкою Decimal", output, StringComparison.Ordinal);
        Assert.Contains("FAMILY=Numeric|Numeric|String\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Реальна_модель_Land_стає_1_0_17_0_за_планом()
    {
        var output = Run("""
            $path = Join-Path (Join-Path (Join-Path (Join-Path (Join-Path $root 'docs') 'delivery') 'reference-data') 'land-template') 'land-template.model.json'
            $mdl = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
            $spec = ([IO.File]::ReadAllText((Join-Path $land 'land-year-totals.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json).totals
            $all = { param($m) @($m.sheets | ForEach-Object { $_.tables } | ForEach-Object { $_.columns }) }
            $r = Convert-LandYearMonthsModel -Model $mdl -MonthNames $months -Totals $spec
            $sum = { param($n) [int](@($r.Tables) | Measure-Object -Property $n -Sum).Sum }
            Write-Host ('ERR=' + ((@($r.Errors)) -join ';'))
            Write-Host ('COLS=' + (& $all $mdl).Count + '->' + (& $all $r.Model).Count)
            Write-Host ('STAT=' + @($r.Tables).Count + '|' + (& $sum 'Added') + '|' + (& $sum 'Removed') + '|' + (& $sum 'Selectors') + '|' + (& $sum 'Varying') + '|' + (& $sum 'Calculated') + '|' + (& $sum 'Totals'))
            Write-Host ('PLAN=' + @($r.Bindings).Count + '|' + @($r.Aliases).Count + '|' + @($r.Groups).Count)
            $nonMonthCalc = @((& $all $r.Model) | Where-Object { $_.dataType -eq 'Calculated' -and -not $_.month }).Count
            $varLeft = @($r.Model.sheets | ForEach-Object { $_.tables } | Where-Object { $_.layout -eq 'MonthsInColumns' } | ForEach-Object { $_.columns } |
                Where-Object { $_.dataType -eq 'Formula' -and -not $_.month -and ([string]$_.expression -match '\[Period\]\.(?!Year\b)\w+|\[Period:') }).Count
            Write-Host ('LEFT=' + $nonMonthCalc + '|' + $varLeft)
            $t7b = Get-Table $r.Model 'T7b_00'
            Write-Host ('T7B=' + ((@($t7b.columns | Where-Object { $_.yearMonthsOf -eq 'ECW_EC_tons' } | ForEach-Object { $_.code })) -join ','))
            $t3 = Get-Table $r.Model 'T3_00'
            $i = [array]::IndexOf(@($t3.columns | ForEach-Object { $_.code }), 'ECW_EC_tons_NOx_M01')
            Write-Host ('T3NOX=' + ((@($t3.columns)[($i - 1)..($i + 11)] | ForEach-Object { $_.code }) -join ','))
            $s7 = @(@($r.Model.sheets | Where-Object { $_.code -eq 'S07_0_WATER_CONSOLIDATION' })[0].tables | Where-Object { $_.code -eq 'T7_00' })[0]
            $s9 = Get-Table $r.Model 'T9b_00'
            Write-Host ('XLSM=' + ((@($s7.columns | Select-Object -Last 2 | ForEach-Object { $_.code })) -join ',') + '|' + ((@($s9.columns | Select-Object -Last 2 | ForEach-Object { $_.code })) -join ','))
            $v = Test-LandYearMonthsCodes -Model $r.Model
            Write-Host ('R3=' + @($v).Count)
            $r2 = Convert-LandYearMonthsModel -Model $r.Model -MonthNames $months -Totals $spec
            Write-Host ('SECOND=' + $r2.Changes + '|' + @($r2.Errors).Count + '|' + @($r2.Bindings).Count + '|' + @($r2.Aliases).Count)
            """);

        Assert.Contains("ERR=\n", output, StringComparison.Ordinal);
        // план LAND-YEAR §a.1: 6 080 -> 14 119 (+8 904 = (669 + 73) × 12, −865 = 669 + 73 + 123)
        Assert.Contains("COLS=6080->14119\n", output, StringComparison.Ordinal);
        Assert.Contains("STAT=53|8904|865|123|73|669|0\n", output, StringComparison.Ordinal);
        // прив'язок-цілей 669 × 12; псевдонімів (123 + 73) × 12 = 2 352 (cfg.MonthInputAlias, L0); груп 669 + 73
        Assert.Contains("PLAN=8028|2352|742\n", output, StringComparison.Ordinal);
        Assert.Contains("LEFT=0|0\n", output, StringComparison.Ordinal);
        Assert.Contains("T7B=" + M12("ECW_EC_tons") + "\n", output, StringComparison.Ordinal);
        // рішення 1: вихід за виходом — після M12 попереднього виходу йдуть 12 місяців NOx
        Assert.Contains("T3NOX=ECW_EC_tons_703_M12," + M12("ECW_EC_tons_NOx") + "\n", output, StringComparison.Ordinal);
        // два підсумки xlsm на своєму місці: 7.0 «Annual» після Q4, 9b «TOTAL» після December
        Assert.Contains("XLSM=Q4,Annual|December,TOTAL\n", output, StringComparison.Ordinal);
        Assert.Contains("R3=0\n", output, StringComparison.Ordinal);
        Assert.Contains("SECOND=0|0|8028|2352\n", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Смуги_місяців_ставлять_місяці_результату_в_групу_виходу()
    {
        var output = Run("""
            $cols = @()
            $o = 0
            foreach ($m in 1..2) { $o++; $cols += [pscustomobject]@{ Code = ('X_M{0:00}' -f $m); Ordinal = $o; MonthNumber = $m; GroupCode = $null } }
            $o++; $cols += [pscustomobject]@{ Code = 'DIY'; Ordinal = $o; MonthNumber = $null; GroupCode = $null }
            foreach ($m in 1..12) { $o++; $cols += [pscustomobject]@{ Code = ('Out_A_M{0:00}' -f $m); Ordinal = $o; MonthNumber = $m; GroupCode = $null } }
            $src = [pscustomobject]@{ months = $months; parents = @() }
            $og = @([pscustomobject]@{ Code = 'MB_O_Out_A'; En = 'Out A'; Ru = $null; Columns = @(1..12 | ForEach-Object { 'Out_A_M{0:00}' -f $_ }) })
            $p = Get-LandMonthBandPlan -TableCode 'A' -Columns $cols -ExistingGroups @() -Source $src -OutputGroups $og
            Write-Host ('ERR=' + @($p.Errors).Count + '|' + $p.MonthGroups + '|' + $p.OutputGroups)
            Write-Host ('GROUPS=' + ((@($p.Groups | ForEach-Object { "$($_.code):$($_.labelL10n.en)" })) -join ','))
            Write-Host ('ASSIGN=' + ((@($p.Changes | ForEach-Object { "$($_.Column)>$($_.To)" })) -join ','))
            $p2 = Get-LandMonthBandPlan -TableCode 'A' -Columns $cols -ExistingGroups @() -Source $src
            Write-Host ('WITHOUT=' + $p2.MonthGroups)
            $bad = Get-LandMonthBandPlan -TableCode 'A' -Columns $cols -ExistingGroups @() -Source $src -OutputGroups @([pscustomobject]@{ Code = 'MB_O_Bad'; En = 'x'; Columns = @('X_M01', 'Out_A_M01') })
            Write-Host ('NONADJ=' + ((@($bad.Errors)) -join ';'))
            """);

        Assert.Contains("ERR=0|2|1\n", output, StringComparison.Ordinal);
        Assert.Contains("GROUPS=MB_M01:January,MB_M02:February,MB_O_Out_A:Out A\n", output, StringComparison.Ordinal);
        Assert.Contains(
            "ASSIGN=X_M01>MB_M01,X_M02>MB_M02," + string.Join(",", Enumerable.Range(1, 12).Select(m => $"Out_A_M{m:00}>MB_O_Out_A")) + "\n",
            output,
            StringComparison.Ordinal);
        // без групи виходу кожен місяць результату став би окремою смугою «January»…«December»
        Assert.Contains("WITHOUT=14\n", output, StringComparison.Ordinal);
        Assert.Contains("NONADJ=група MB_O_Bad: колонки не суміжні або їх бракує", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_YearMonths_лише_план_поки_немає_L0()
    {
        var output = Run("""
            $build = Join-Path $land 'Build-LandTemplate.ps1'
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($build, [ref]$null, [ref]$null)
            $fn = $ast.Find({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Stage-YearMonths' }, $true)
            if (-not $fn) { throw 'Stage-YearMonths не знайдено' }
            $here = $land; $repo = $root; $YearMonthsPlanOut = $null
            function Log([string]$m) { Write-Host ('LOG=' + $m) }
            . ([scriptblock]::Create($fn.Extent.Text))
            function Try-Stage { try { Stage-YearMonths; return 'OK' } catch { return $_.Exception.Message } }
            $script:Mdl = New-Fixture
            $Stage = @('YearMonths'); $DryRun = $false; $Layout = 'PerMonthCopies'
            Write-Host ('NOLAYOUT=' + (Try-Stage))
            $Layout = 'YearWithMonthColumns'; $Stage = @('YearMonths', 'Template')
            Write-Host ('WITHTEMPLATE=' + (Try-Stage))
            $YearMonthsPlanOut = Join-Path $root 'plan.json'; $Stage = @('YearMonths')
            Write-Host ('INREPO=' + (Try-Stage))
            $YearMonthsPlanOut = $null
            Write-Host ('ALONE=' + (Try-Stage) + '|' + $script:Mdl.yearMonthsTargetVersion + '|' + @((Get-Table $script:Mdl 'A').columns).Count)
            """);

        Assert.Contains("NOLAYOUT=-Stage YearMonths потребує -Layout YearWithMonthColumns", output, StringComparison.Ordinal);
        Assert.Contains("WITHTEMPLATE=-Stage YearMonths разом з етапами (Template) поки заборонено", output, StringComparison.Ordinal);
        Assert.Contains("(L0)", output, StringComparison.Ordinal);
        Assert.Contains("INREPO=-YearMonthsPlanOut усередині репозиторію", output, StringComparison.Ordinal);
        // 13 + DIY + Fuel… : Fuel, X×12, Dens/Days/Tr ×12, DIY, Out_A/Out_B ×12 = 1 + 12 + 36 + 1 + 24
        Assert.Contains("ALONE=OK|1.0.17.0|74\n", output, StringComparison.Ordinal);
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

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-yearmonths-{Guid.NewGuid():N}.ps1");
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
