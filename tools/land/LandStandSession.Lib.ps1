# tools/land/LandStandSession.Lib.ps1
# Спільне для точкових інструментів вікна паритету (Load-LandInputCells.ps1, Invoke-LandRecalc.ps1,
# New-LandMethodologyDraft.ps1): охорона стенда, пошук документів за ІМЕНЕМ, повтор на 429.
# Потребує EcrApi.ps1 (dot-source раніше). Значень прода й секретів тут немає.

function Initialize-LandStandSession {
    <#
    .SYNOPSIS
        Охорона стенда ДО будь-якого виклику: лише локальний API; для запису (-ForWrite) - явний -SqlConnection
        (локальний сервер, база EcrLand*) і -ConfirmStand у формі `<адреса>/<база>`.
    .DESCRIPTION
        -SqlConnection потрібен не для запису в SQL (ці інструменти ходять лише в API), а щоб ім'я бази стенда
        було названо явно: URL API бази не називає, і підтвердження лише адреси не відрізнило б стенд від
        локального прод-інстансу (див. Set-EcrStandConfirmation). Дефолту на стенд немає.
    .OUTPUTS
        string - ім'я бази стенда (порожній рядок, якщо -ForWrite не задано).
    #>
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$ConfirmStand,
        [string]$SqlConnection,
        [switch]$ForWrite
    )

    Assert-LocalBaseUrl $BaseUrl
    Assert-LandConfirmStandNamesStand -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand
    if (-not $ForWrite) { return '' }
    if ([string]::IsNullOrWhiteSpace($SqlConnection)) {
        throw 'Запис потребує явного -SqlConnection (локальний сервер, база EcrLand*): скрипт не має стенда за замовчуванням. Без -Execute інструмент нічого не змінює. Відмова.'
    }
    $standDb = Assert-LocalSqlConnection $SqlConnection
    Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl -Database $standDb
    return $standDb
}

function Get-LandRetryAfterSeconds {
    <# Retry-After відповіді (ціле число секунд, мін. 1); без заголовка - $DefaultSeconds. #>
    param($Response, [int]$DefaultSeconds = 8)
    if ($null -ne $Response -and $null -ne $Response.Headers) {
        foreach ($k in @($Response.Headers.Keys)) {
            if ([string]$k -ieq 'Retry-After') {
                $n = 0
                if ([int]::TryParse(([string]$Response.Headers[$k]).Trim(), [ref]$n) -and $n -ge 1) { return $n }
            }
        }
    }
    return $DefaultSeconds
}

function Invoke-LandEcrRetry {
    <#
    .SYNOPSIS
        Invoke-Ecr з повтором на 429 (квота): чекає Retry-After, не більше $MaxAttempts спроб. Інші відповіді -
        як є (рішення про помилку приймає викликач). Повертає останню відповідь.
    #>
    param(
        [Parameter(Mandatory)][string]$HttpMethod,
        [Parameter(Mandatory)][string]$ApiPath,
        $RequestBody = $null,
        [int]$MaxAttempts = 30
    )
    $resp = $null
    for ($i = 0; $i -lt $MaxAttempts; $i++) {
        if ($null -eq $RequestBody) { $resp = Invoke-Ecr -Method $HttpMethod -Path $ApiPath }
        else { $resp = Invoke-Ecr -Method $HttpMethod -Path $ApiPath -Body $RequestBody }
        if ($resp.Status -ne 429) { return $resp }
        Start-Sleep -Seconds (Get-LandRetryAfterSeconds -Response $resp)
    }
    return $resp
}

function Assert-LandConfirmStandNamesStand {
    <#
    .SYNOPSIS
        -ConfirmStand обов'язковий і в плані (-DryRun): має називати саме цей стенд (адреса API, host:port або host,
        за бажання з `/<база>`). Лише перевірка: підтвердження запису вмикає Set-EcrStandConfirmation (-Execute).
    #>
    param([Parameter(Mandatory)][string]$BaseUrl, [string]$ConfirmStand)

    if ([string]::IsNullOrWhiteSpace($ConfirmStand)) {
        throw "Потрібен явний -ConfirmStand <імʼя або URL стенду> (наприклад '$($BaseUrl.TrimEnd('/'))/<база>'). Скрипт не має стенда за замовчуванням. Відмова."
    }
    $u = [System.Uri]$BaseUrl
    $given = $ConfirmStand.Trim().TrimEnd('/')
    $schemeEnd = $given.IndexOf('://', [StringComparison]::Ordinal)
    $searchFrom = if ($schemeEnd -ge 0) { $schemeEnd + 3 } else { 0 }
    $slash = $given.IndexOf('/', $searchFrom, [StringComparison]::Ordinal)
    $head = if ($slash -ge 0) { $given.Substring(0, $slash) } else { $given }
    $wanted = @($BaseUrl.TrimEnd('/'), $u.Authority, $u.Host) | ForEach-Object { $_.ToLowerInvariant() }
    if ($wanted -notcontains $head.ToLowerInvariant()) {
        throw "-ConfirmStand '$ConfirmStand' не збігається зі стендом (очікується '$($u.Authority)' або '$($BaseUrl.TrimEnd('/'))', за бажання з /<база>). Відмова."
    }
}

function ConvertFrom-LandDocMap {
    <#
    .SYNOPSIS
        -DocMap: JSON `{"<ім'я документа>": <id>, ...}` (рядок або шлях до файла) -> впорядкована хеш-таблиця ім'я -> id.
        Документ шукається за мапою, а не за іменем на стенді: id задає людина, яка склала мапу.
    #>
    param([Parameter(Mandatory)][string]$Json)

    $text = $Json
    if ($text -notmatch '^\s*\{' -and (Test-Path -LiteralPath $text -PathType Leaf)) {
        $text = Get-Content -LiteralPath $text -Raw -Encoding UTF8
    }
    $obj = $null
    try { $obj = $text | ConvertFrom-Json } catch { throw "-DocMap не JSON: $($_.Exception.Message)" }
    if ($null -eq $obj -or $obj -isnot [System.Management.Automation.PSCustomObject]) {
        throw '-DocMap: очікується JSON-обʼєкт {"<імʼя документа>": <id>, ...}.'
    }
    $map = [ordered]@{}
    foreach ($prop in $obj.PSObject.Properties) {
        $name = ([string]$prop.Name).Trim()
        $parsedId = 0
        if ($name.Length -eq 0 -or -not [int]::TryParse(([string]$prop.Value), [ref]$parsedId) -or $parsedId -le 0) {
            throw "-DocMap: для '$($prop.Name)' потрібен додатний цілий id."
        }
        $map[$name] = $parsedId
    }
    if ($map.Count -eq 0) { throw '-DocMap порожня.' }
    return $map
}