# tools/hse401/Hse401Rows.Lib.ps1
# Побудова вхідних комірок рядка HSE401 із витягу: мапа довідників, округлення шуму float, JSON для PATCH.

Set-StrictMode -Version 2.0

$script:HseInv = [Globalization.CultureInfo]::InvariantCulture
$script:HseScale = @{ H_LIMIT_T = 12; H_LIMIT_GS = 12; H_RAW_T = 12; H_RUN_H = 6; H_BASIS = 3 }   # D** = 4

function ConvertTo-HseNormText([string]$s) {
    $x = ([string]$s) -replace '[\u2010-\u2015\u2212]', '-' -replace '[\u00A0\u2007\u202F]', ' ' -replace '[\u00AB\u00BB\u201C\u201D\u201E]', '"' -replace '\s+', ' '
    return $x.Trim().ToLowerInvariant()
}

function Get-HseListPlan {
    <# Записи списку HSE401_*: значення словника (за порядком, без дублів за нормалізованим текстом) + "зайві" значення рядків
       (яких немає у словнику; код <PFX>X<nn>). Спільне для Build-Hse401Registries.ps1 і Get-HseLookupMaps. #>
    param($Extract, [string]$Key, [string]$Pfx, [string]$Xl)
    $plan = New-Object System.Collections.Generic.List[object]
    $seen = @{}; $i = 0
    foreach ($txt in $Extract['dictionaries'][$Key]) {
        $i++
        $n = ConvertTo-HseNormText $txt
        if ($seen.ContainsKey($n)) { continue }
        $seen[$n] = $true
        $plan.Add([pscustomobject]@{ Text = (([string]$txt) -replace '\s+', ' ').Trim(); Norm = $n; Code = ('{0}{1:00}' -f $Pfx, $i); Index = $i; IsExtra = $false })
    }
    $x = 0
    foreach ($row in $Extract['rows']) {
        $v = $row['v']
        if (-not $v.ContainsKey($Xl)) { continue }
        $n = ConvertTo-HseNormText $v[$Xl]
        if ($n.Length -eq 0 -or $seen.ContainsKey($n)) { continue }
        $seen[$n] = $true; $x++
        $plan.Add([pscustomobject]@{ Text = (([string]$v[$Xl]) -replace '\s+', ' ').Trim(); Norm = $n; Code = ('{0}X{1:00}' -f $Pfx, $x); Index = 0; IsExtra = $true })
    }
    return , $plan.ToArray()
}

function Get-HseLookupMaps {
    <# Мапи "нормалізований текст словника" -> id запису для списків HSE401_* і "E<код>" -> id для речовин; NAE із карти. #>
    param($Extract, [string]$NaeMapFile)
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $lists = @(
        @{ code = 'HSE401_REGION'; key = 'region'; pfx = 'REG'; xl = 'C' }, @{ code = 'HSE401_ONOFF'; key = 'onoff'; pfx = 'OON'; xl = 'E' },
        @{ code = 'HSE401_AREA'; key = 'area'; pfx = 'ARE'; xl = 'F' }, @{ code = 'HSE401_FACILITY'; key = 'facility'; pfx = 'FAC'; xl = 'G' },
        @{ code = 'HSE401_ACTIVITY'; key = 'activity'; pfx = 'ACT'; xl = 'H' }, @{ code = 'HSE401_PERMIT'; key = 'permit'; pfx = 'PRM'; xl = 'I' },
        @{ code = 'HSE401_EQUIPTYPE'; key = 'equip'; pfx = 'EQT'; xl = 'J' }, @{ code = 'HSE401_SRCTYPE'; key = 'srctype'; pfx = 'SRT'; xl = 'K' })
    $maps = @{}
    foreach ($l in $lists) {
        $byCode = @{}
        foreach ($e in @((Invoke-HseOk GET "/api/v1/registries/$($l.code)/entries?asOf=$today").Json)) { $byCode[([string]$e.code).ToUpperInvariant()] = [long]$e.id }
        $m = @{}
        foreach ($it in (Get-HseListPlan -Extract $Extract -Key $l.key -Pfx $l.pfx -Xl $l.xl)) { if ($byCode.ContainsKey($it.Code.ToUpperInvariant())) { $m[$it.Norm] = $byCode[$it.Code.ToUpperInvariant()] } }
        $maps[$l.xl] = $m
    }
    $pm = @{}
    foreach ($e in @((Invoke-HseOk GET "/api/v1/registries/LAND_AIR_SUBSTANCE/entries?asOf=$today").Json)) { $pm[([string]$e.code).ToUpperInvariant()] = [long]$e.id }
    $maps['O'] = $pm
    $maps['NAE'] = (Read-HseJson $NaeMapFile)['map']
    return $maps
}

function ConvertTo-HseNaeCode([string]$raw) {
    $t = ([string]$raw -replace '\s+', ' ').Trim()
    if ($t -match '^[0-9]+$' -and $t.Length -lt 4) { return $t.PadLeft(4, '0') }
    return $t
}

function ConvertTo-HseEn([string]$s) { $b = Split-HseBilingual $s; if ($b.En) { return $b.En } else { return $s.Trim() } }

function Convert-HseNumber {
    <# Сире значення Excel -> [decimal] до шкали колонки. Повертає @{ Value; Noise(bool); Raw } ; шум = значення змінилось при G15-округленні/шкалі. #>
    param([string]$Raw, [int]$Scale)
    $d = [double]::Parse($Raw, [Globalization.NumberStyles]::Float, $script:HseInv)
    $g15 = [double]::Parse($d.ToString('G15', $script:HseInv), [Globalization.NumberStyles]::Float, $script:HseInv)
    $dec = [decimal]::Parse($g15.ToString('G15', $script:HseInv), [Globalization.NumberStyles]::Float, $script:HseInv)
    $rounded = [Math]::Round($dec, $Scale, [MidpointRounding]::AwayFromZero)
    $noise = ($g15 -ne $d) -or ($rounded -ne $dec)
    return @{ Value = $rounded; Noise = $noise; Raw = $Raw }
}

function Get-HseRowCells {
    <# Вхідні комірки рядка: список @{ col; kind (s|n|i|l); val }. Лишає лог округлень і невідповідностей у $Log. #>
    param($Row, $Maps, $Log)
    $v = $Row['v']
    $cells = New-Object System.Collections.Generic.List[object]
    $rn = $Row['r']
    if ($v.ContainsKey('B')) { $cells.Add(@{ col = 'H_NO'; kind = 'n'; val = [string]([long][double]::Parse($v['B'], $script:HseInv)) }) }
    foreach ($pair in @(@('C', 'H_REGION'), @('E', 'H_ONOFF'), @('F', 'H_AREA'), @('G', 'H_FACILITY'), @('H', 'H_ACTIVITY'), @('I', 'H_PERMIT'), @('J', 'H_EQUIP'), @('K', 'H_SRCTYPE'))) {
        $xl = $pair[0]
        if (-not $v.ContainsKey($xl)) { continue }
        $norm = ConvertTo-HseNormText $v[$xl]
        if ($Maps[$xl].ContainsKey($norm)) { $cells.Add(@{ col = $pair[1]; kind = 'l'; val = [string]$Maps[$xl][$norm] }) }
        else { $Log.unmapped.Add([pscustomobject]@{ row = $rn; col = $xl; text = $norm }) }
    }
    if ($v.ContainsKey('D')) { $cells.Add(@{ col = 'H_INFO'; kind = 's'; val = ([string]$v['D']) }) }
    if ($v.ContainsKey('C') -and $v.ContainsKey('E') -and $v.ContainsKey('L')) {
        $k = "$(ConvertTo-HseEn $v['C'])|$(ConvertTo-HseEn $v['E'])|$(ConvertTo-HseNaeCode $v['L'])"
        if ($Maps['NAE'].ContainsKey($k)) { $cells.Add(@{ col = 'H_NAE'; kind = 'l'; val = [string]$Maps['NAE'][$k] }) }
        else { $Log.unmapped.Add([pscustomobject]@{ row = $rn; col = 'NAE'; text = $k }) }
    }
    if ($v.ContainsKey('O')) {
        $pc = 'E' + ([string]$v['O']).Trim()
        if ($Maps['O'].ContainsKey($pc.ToUpperInvariant())) { $cells.Add(@{ col = 'H_POLL'; kind = 'l'; val = [string]$Maps['O'][$pc.ToUpperInvariant()] }) }
        else { $Log.unmapped.Add([pscustomobject]@{ row = $rn; col = 'O'; text = $pc }) }
    }
    $num = @(@('R', 'H_LIMIT_T', 16), @('S', 'H_LIMIT_GS', 16), @('BC', 'H_RAW_T', 16), @('BD', 'H_RUN_H', 6))
    for ($m = 0; $m -lt 12; $m++) { $num += , @($script:HseMonthWd[$m], ('H_D{0:00}' -f ($m + 1)), 4) }
    foreach ($n in $num) {
        $xl = $n[0]
        if (-not $v.ContainsKey($xl)) { continue }
        $cv = Convert-HseNumber $v[$xl] $n[2]
        if ($cv.Noise) { $Log.round.Add([pscustomobject]@{ row = $rn; col = $n[1]; xl = $xl; raw = $cv.Raw; rounded = $cv.Value.ToString($script:HseInv) }) }
        $cells.Add(@{ col = $n[1]; kind = 'n'; val = $cv.Value.ToString($script:HseInv) })
    }
    if ($v.ContainsKey('BB')) { $cells.Add(@{ col = 'H_RAW_NAME'; kind = 's'; val = [string]$v['BB'] }) }
    if ($null -ne $Row['basis']) { $cells.Add(@{ col = 'H_BASIS'; kind = 'n'; val = ([decimal]$Row['basis']).ToString($script:HseInv) }) }
    return $cells
}

function ConvertTo-HseJsonString([string]$s) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    foreach ($ch in $s.ToCharArray()) {
        switch ($ch) {
            '"' { [void]$sb.Append('\"') } '\' { [void]$sb.Append('\\') }
            default { if ([int]$ch -lt 32) { [void]$sb.AppendFormat('\u{0:x4}', [int]$ch) } else { [void]$sb.Append($ch) } }
        }
    }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-HsePatchJson {
    <# Тіло PATCH cells: origin UserEdit; рядки створюються (baseVersion null). Вручну (швидко для 25k комірок). #>
    param([int]$PeriodKey, [int]$TableInstanceId, $RowKeys, $RowCells)
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('{"origin":"UserEdit","periodKey":').Append($PeriodKey).Append(',"tableInstanceId":').Append($TableInstanceId).Append(',"rows":[')
    for ($i = 0; $i -lt $RowKeys.Count; $i++) {
        if ($i -gt 0) { [void]$sb.Append(',') }
        [void]$sb.Append('{"rowKey":"').Append($RowKeys[$i]).Append('","baseVersion":null,"cells":[')
        $first = $true
        foreach ($c in $RowCells[$i]) {
            if (-not $first) { [void]$sb.Append(',') }; $first = $false
            [void]$sb.Append('{"columnCode":"').Append($c.col).Append('","isEmpty":false,"value":')
            if ($c.kind -eq 'n') { [void]$sb.Append($c.val) } else { [void]$sb.Append((ConvertTo-HseJsonString $c.val)) }
            [void]$sb.Append('}')
        }
        [void]$sb.Append(']}')
    }
    [void]$sb.Append(']}')
    return $sb.ToString()
}

function Invoke-HseRawJson {
    <# PATCH/POST з готовим JSON-текстом через HttpClient сесії EcrApi (з повтором на 429/503). #>
    param([string]$Method, [string]$Path, [string]$Json)
    for ($try = 0; $try -lt 40; $try++) {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), ((Get-Hse401BaseUrl) + $Path))
        $req.Content = New-Object System.Net.Http.StringContent($Json, [System.Text.Encoding]::UTF8, 'application/json')
        $resp = $script:EcrClient.SendAsync($req).GetAwaiter().GetResult()
        $status = [int]$resp.StatusCode
        $text = [string]$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $wait = 4
        foreach ($h in $resp.Headers) { if ($h.Key -ieq 'Retry-After') { $n = 0; if ([int]::TryParse(($h.Value -join ''), [ref]$n) -and $n -ge 1) { $wait = $n } } }
        $resp.Dispose(); $req.Dispose()
        if ($status -eq 429 -or $status -eq 503) { Start-Sleep -Seconds $wait; continue }
        $j = $null; if ($text) { try { $j = $text | ConvertFrom-Json } catch { } }
        return [pscustomobject]@{ Status = $status; Text = $text; Json = $j }
    }
    throw "429/503 не минув після 40 спроб: $Method $Path"
}
