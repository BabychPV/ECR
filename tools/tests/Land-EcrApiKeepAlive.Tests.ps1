<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/EcrApi.ps1 — keepalive сеансу (D-471: GET не продовжує
    сеанс; F13 #804: план Add-LandMonthGroups -WhatIf падав 401 ECR-AUTH-0401 через ~5 хв) і кількість читань
    Get-LandBandTables. Мережі немає: Send-EcrHttp підмінено сервером з годинником і межею бездіяльності.
#>

Describe 'EcrApi: keepalive сеансу під час довгих прогонів' {
    It 'довгий прогін самих GET не отримує 401: сигнал активності йде не рідше EcrKeepAliveSeconds' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/EcrApi.ps1')
        Set-StrictMode -Off
        $script:FakeNow = [DateTime]'2026-11-10T10:00:00Z'
        $script:EcrClock = { $script:FakeNow }
        $script:ServerLastAct = $script:FakeNow
        $script:Calls = New-Object System.Collections.ArrayList
        function Send-EcrHttp {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path")
            if ($Path -eq '/api/v1/session/activity') { $script:ServerLastAct = $script:FakeNow; return [pscustomobject]@{ Status = 204; Text = ''; Headers = @{} } }
            $idle = ($script:FakeNow - $script:ServerLastAct).TotalMinutes
            $script:FakeNow = $script:FakeNow.AddSeconds(3)          # кожен GET «триває» 3 с
            if ($idle -gt 5) { return [pscustomobject]@{ Status = 401; Text = '{"errorCode":"ECR-AUTH-0401"}'; Headers = @{} } }
            return [pscustomobject]@{ Status = 200; Text = '{}'; Headers = @{} }
        }
        $script:EcrLastActivity = $script:FakeNow                   # стан після Connect-Ecr
        $bad = 0
        foreach ($i in 1..400) { $r = Invoke-Ecr -Method GET -Path "/api/v1/x/$i"; if ($r.Status -ne 200) { $bad++ } }   # 20 хв
        $ka = @($script:Calls | Where-Object { $_ -eq 'POST /api/v1/session/activity' }).Count
        if ($bad -ne 0) { throw "401 після бездіяльності: $bad із 400 GET (keepalive не спрацював)" }
        if ($ka -lt 20 -or $ka -gt 30) { throw "сигналів активності $ka, очікувалось ~27 (раз на 45 с за 20 хв)" }
    }

    It 'без входу (Connect-Ecr не викликано) сигнал активності не шлеться; він не потребує -ConfirmStand' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/EcrApi.ps1')
        Set-StrictMode -Off
        $script:FakeNow = [DateTime]'2026-11-10T10:00:00Z'
        $script:EcrClock = { $script:FakeNow }
        $script:Calls = New-Object System.Collections.ArrayList
        function Send-EcrHttp {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path"); $script:FakeNow = $script:FakeNow.AddMinutes(1)
            return [pscustomobject]@{ Status = 200; Text = '{}'; Headers = @{} }
        }
        foreach ($i in 1..3) { $null = Invoke-Ecr -Method GET -Path '/api/v1/a' }
        if (@($script:Calls | Where-Object { $_ -like '*session/activity' }).Count -ne 0) { throw 'keepalive без входу' }
        $script:EcrLastActivity = $script:FakeNow
        foreach ($i in 1..3) { $null = Invoke-Ecr -Method GET -Path '/api/v1/a' }   # -ConfirmStand не задано (план -WhatIf)
        if (@($script:Calls | Where-Object { $_ -like '*session/activity' }).Count -lt 2) { throw 'keepalive у режимі лише читання' }
    }

    It 'Get-LandBandTables (сервер з isMonthColumn/monthNumber у структурі): рівно ОДИН запит на всю версію' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/EcrApi.ps1')
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        Set-StrictMode -Off
        $script:Calls = New-Object System.Collections.ArrayList
        $structure = '{"sheets":[{"code":"S1","tables":[{"id":1,"code":"T1","headerGroups":[],"columns":[{"id":11,"code":"A","ordinal":1,"isMonthColumn":true,"monthNumber":1},{"id":12,"code":"B","ordinal":2,"isMonthColumn":false,"monthNumber":null}]},{"id":2,"code":"T2","headerGroups":[],"columns":[{"id":21,"code":"C","ordinal":1,"isMonthColumn":true,"monthNumber":12}]}]}]}'
        function Send-EcrHttp {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path")
            if ($Path -like '*/structure') { return [pscustomobject]@{ Status = 200; Text = $structure; Headers = @{} } }
            return [pscustomobject]@{ Status = 500; Text = ''; Headers = @{} }
        }
        $t = Get-LandBandTables -VersionId 7
        if (@($script:Calls).Count -ne 1) { throw "запитів $(@($script:Calls).Count), очікувався 1: $($script:Calls -join '; ')" }
        $mo = @{}; foreach ($x in $t) { foreach ($c in $x.Columns) { $mo[$c.Code] = $c.MonthNumber } }
        if ($mo['A'] -ne 1 -or $null -ne $mo['B'] -or $mo['C'] -ne 12) { throw 'місяці зі структури' }
    }

    It 'Get-LandBandTables (старіший сервер без isMonthColumn у структурі): структура один раз, далі рівно одне читання на колонку' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/EcrApi.ps1')
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        Set-StrictMode -Off
        $script:FakeNow = [DateTime]'2026-11-10T10:00:00Z'
        $script:EcrClock = { $script:FakeNow }
        $script:Calls = New-Object System.Collections.ArrayList
        $structure = '{"sheets":[{"code":"S1","tables":[{"id":1,"code":"T1","headerGroups":[],"columns":[{"id":11,"code":"A","ordinal":1},{"id":12,"code":"B","ordinal":2}]},{"id":2,"code":"T2","headerGroups":[],"columns":[{"id":21,"code":"C","ordinal":1}]}]}]}'
        function Send-EcrHttp {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path"); $script:FakeNow = $script:FakeNow.AddSeconds(30)
            if ($Path -like '*/structure') { return [pscustomobject]@{ Status = 200; Text = $structure; Headers = @{} } }
            if ($Path -like '*/columns/*') { return [pscustomobject]@{ Status = 200; Text = '{"isMonthColumn":true,"monthNumber":1}'; Headers = @{} } }
            return [pscustomobject]@{ Status = 204; Text = ''; Headers = @{} }
        }
        $script:EcrLastActivity = $script:FakeNow
        $t = Get-LandBandTables -VersionId 7
        $gets = @($script:Calls | Where-Object { $_ -like 'GET *' })
        if (@($gets | Where-Object { $_ -like '*/structure' }).Count -ne 1) { throw 'структура мусить читатися один раз' }
        if (@($gets | Where-Object { $_ -like '*/columns/*' }).Count -ne 3) { throw "читань колонок $(@($gets).Count - 1), очікувалось 3" }
        if (@($t).Count -ne 2 -or @($t[0].Columns | Where-Object { $_.MonthNumber -eq 1 }).Count -ne 2) { throw 'таблиці/місяці' }
        if (@($script:Calls | Where-Object { $_ -like '*session/activity' }).Count -lt 1) { throw 'keepalive під час читання колонок' }
    }
}
