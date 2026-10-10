<#
    Pester 5: tools/land/Apply-ContractHeader.ps1 (N5-03, аудит 2026-10-09).

    Скрипт копіюється в тимчасовий каталог разом із ПІДМІНЕНИМ EcrApi.ps1: справжній файл
    підключається повністю, а потім Connect-Ecr стає порожнім, і Invoke-Ecr переходить на
    пам'ятний «сервер» (Invoke-FakeEcr нижче). Мережі й ECR не потрібно. Усі виклики
    записуються в $global:FakeEcr.Calls, тож тести перевіряють і відмови, і те, що нічого не записано.

    Запуск: Invoke-Pester -Path tools/tests (Pester 5+). У конвеєрі — крок verify-all.ps1
    «Тести інструментів (Pester)».
#>

BeforeAll {
    # Спільні дані тестів — у глобальному $EcrTest: допоміжні функції нижче глобальні, і їхня область
    # `$script:` не гарантовано збігається з областю цього файла.
    $repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
    $realEcrApi = Join-Path $repoRoot 'tools/land/EcrApi.ps1'
    $dataDir = Join-Path $repoRoot 'docs/delivery/reference-data/land-contract'
    $manifest = Get-Content -LiteralPath (Join-Path $dataDir 'header-fields.json') -Raw -Encoding UTF8 | ConvertFrom-Json

    # id довідників так, як їх віддав би сервер.
    $registryIds = @{}
    $n = 100
    foreach ($f in @($manifest.fields | Where-Object { $_.kind -eq 'Lookup' })) {
        if (-not $registryIds.ContainsKey([string]$f.registry)) { $registryIds[[string]$f.registry] = $n++ }
    }

    # Копія скрипта + підмінений EcrApi.ps1 у $TestDrive.
    $sandbox = Join-Path $TestDrive 'land'
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'tools/land/Apply-ContractHeader.ps1') -Destination $sandbox
    $quotedApi = $realEcrApi.Replace("'", "''")
    $stub = @"
. '$quotedApi'
function Connect-Ecr { param([string]`$BaseUrl, [string]`$User, [securestring]`$Password) }
function Invoke-Ecr {
    param([string]`$Method, [string]`$Path, `$Body = `$null, [hashtable]`$Headers = @{})
    Invoke-FakeEcr -Method `$Method -Path `$Path -Body `$Body
}
"@
    Set-Content -LiteralPath (Join-Path $sandbox 'EcrApi.ps1') -Value $stub -Encoding UTF8
    $global:EcrTest = @{
        ScriptPath = Join-Path $sandbox 'Apply-ContractHeader.ps1'
        DataDir    = $dataDir
        Manifest   = $manifest
        Registries = $registryIds
    }

    function global:New-FakeResponse {
        param([int]$Status, $Json = $null)
        if ($null -ne $Json) { $Json = (ConvertTo-Json -InputObject $Json -Depth 12 -Compress) | ConvertFrom-Json }
        return [pscustomobject]@{ Status = $Status; Json = $Json; Text = ''; Headers = @{} }
    }

    # «Сервер» ECR в пам'яті: шаблон Land (id 1), версії, поля шапки, виклики.
    function global:Invoke-FakeEcr {
        param([string]$Method, [string]$Path, $Body = $null)
        $s = $global:FakeEcr
        [void]$s.Calls.Add([pscustomobject]@{ Method = $Method; Path = $Path; Body = $Body })

        if ($Method -eq 'GET' -and $Path.StartsWith('/api/v1/templates?')) {
            return New-FakeResponse 200 @{ items = @(@{ id = 1; code = 'Land' }) }
        }
        if ($Method -eq 'GET' -and $Path -eq '/api/v1/templates/1/versions?limit=100') {
            return New-FakeResponse 200 @{ items = @($s.Versions | ForEach-Object { @{ id = $_.id; version = $_.version; status = $_.status } }) }
        }
        if ($Method -eq 'GET' -and $Path -eq '/api/v1/registries') {
            return New-FakeResponse 200 @($s.Registries.Keys | ForEach-Object { @{ code = $_; id = $s.Registries[$_] } })
        }
        if ($Method -eq 'GET' -and $Path -match '^/api/v1/template-versions/(\d+)/header-fields$') {
            return New-FakeResponse 200 @($s.Fields[[int]$Matches[1]])
        }
        if ($Method -eq 'POST' -and $Path -match '^/api/v1/template-versions/(\d+)/clone$') {
            $srcId = [int]$Matches[1]
            $new = $s.NextId
            $s.NextId = $new + 1
            $s.Versions += @{ id = $new; version = [string]$Body.newVersion; status = 'Draft' }
            $s.Fields[$new] = @($s.Fields[$srcId] | ForEach-Object { $_.Clone() })
            return New-FakeResponse 200 @{ versionId = $new }
        }
        if ($Method -eq 'PUT' -and $Path -match '^/api/v1/template-versions/(\d+)/header-fields/(\w+)$') {
            $id = [int]$Matches[1]
            $code = $Matches[2]
            $row = @{
                code = $code; dataType = [string]$Body.dataType; isRequired = [bool]$Body.isRequired
                lookupRegistryDefId = $Body.lookupRegistryDefId; ordinal = [int]$Body.ordinal
                labelL10n = @{ values = $Body.labelL10n }
            }
            $s.Fields[$id] = @(@($s.Fields[$id] | Where-Object { $_.code -ne $code }) + $row)
            return New-FakeResponse 200 @{}
        }
        if ($Method -eq 'POST' -and $Path -match '^/api/v1/template-versions/(\d+)/publish$') {
            $pubId = [int]$Matches[1]
            ($s.Versions | Where-Object { $_.id -eq $pubId }).status = 'Published'
            return New-FakeResponse 200 @{}
        }
        if ($Method -eq 'POST' -and $Path -match '^/api/v1/documents/(\d+)/migrate-version$') {
            if ($Body.dryRun) { return New-FakeResponse 200 @{ canApply = $true; refusals = @() } }
            return New-FakeResponse 200 @{ transferredValues = 0; lostValues = 0 }
        }
        throw "FakeEcr: непередбачений виклик $Method $Path"
    }

    # Поля шапки, що ТОЧНО відповідають маніфесту (0 змін).
    function global:New-MatchingFields {
        $rows = @()
        foreach ($f in @($global:EcrTest.Manifest.fields | Where-Object { $_.kind -ne 'Service' })) {
            $lookup = $null
            if ($f.kind -eq 'Lookup') { $lookup = $global:EcrTest.Registries[[string]$f.registry] }
            $req = $false
            if ($f.PSObject.Properties.Match('isRequired').Count -gt 0) { $req = [bool]$f.isRequired }
            $rows += @{
                code = [string]$f.key; dataType = [string]$f.kind; isRequired = $req
                lookupRegistryDefId = $lookup; ordinal = [int]$f.position
                labelL10n = @{ values = @{ en = [string]$f.key } }
            }
        }
        return , $rows
    }

    # Нове «середовище»: $Versions — @(@{id; version; status; match = <поля відповідають маніфесту>}).
    function global:Initialize-FakeEcr {
        param([object[]]$Versions)
        $fields = @{}
        foreach ($v in $Versions) {
            if ($v.match) { $fields[[int]$v.id] = New-MatchingFields }
            else { $fields[[int]$v.id] = @() }
        }
        $global:FakeEcr = @{
            Calls      = New-Object System.Collections.ArrayList
            Versions   = @($Versions | ForEach-Object { @{ id = $_.id; version = $_.version; status = $_.status } })
            Fields     = $fields
            Registries = $global:EcrTest.Registries
            NextId     = 50
        }
    }

    function global:Invoke-Header {
        param([hashtable]$Extra = @{})
        & $global:EcrTest.ScriptPath -TemplateCode Land -DataDir $global:EcrTest.DataDir -BaseUrl 'http://localhost:5092' @Extra *> $null
    }

    # Усе, що змінює стан: не-GET виклики, крім dry-run міграції (читання за змістом).
    function global:Get-FakeWrites {
        return @($global:FakeEcr.Calls | Where-Object {
                $_.Method -ne 'GET' -and -not ($_.Path -like '*/migrate-version' -and $_.Body.dryRun)
            })
    }
}

AfterAll {
    foreach ($f in 'New-FakeResponse', 'Invoke-FakeEcr', 'New-MatchingFields', 'Initialize-FakeEcr', 'Invoke-Header', 'Get-FakeWrites') {
        Remove-Item -LiteralPath "Function:\$f" -ErrorAction SilentlyContinue
    }
    Remove-Variable -Name FakeEcr, EcrTest -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Apply-ContractHeader.ps1: наявна чернетка (N5-03)' {
    It 'без -UseExistingDraft зупиняється і нічого не записує' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false },
            @{ id = 2; version = '1.0.1.0'; status = 'Draft'; match = $false }
        )
        { Invoke-Header } | Should -Throw -ExpectedMessage '*чернетку 1.0.1.0*-UseExistingDraft*'
        Get-FakeWrites | Should -HaveCount 0
    }

    It 'зупиняється й у -DryRun: «було б застосовано» вводило б в оману' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false },
            @{ id = 2; version = '1.0.1.0'; status = 'Draft'; match = $false }
        )
        { Invoke-Header @{ DryRun = $true } } | Should -Throw -ExpectedMessage '*-UseExistingDraft*'
        Get-FakeWrites | Should -HaveCount 0
    }

    It '-SourceVersionId разом із чернеткою — помилка, навіть із -UseExistingDraft' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false },
            @{ id = 2; version = '1.0.1.0'; status = 'Draft'; match = $false }
        )
        { Invoke-Header @{ SourceVersionId = 1; UseExistingDraft = $true } } | Should -Throw -ExpectedMessage '*-SourceVersionId 1*'
        Get-FakeWrites | Should -HaveCount 0
    }

    It 'з -UseExistingDraft довносить поля в ЦЮ чернетку й публікує її без клонування' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false },
            @{ id = 2; version = '1.0.1.0'; status = 'Draft'; match = $false }
        )
        Invoke-Header @{ UseExistingDraft = $true }

        $writes = Get-FakeWrites
        @($writes | Where-Object { $_.Path -like '*/clone' }) | Should -HaveCount 0
        $puts = @($writes | Where-Object { $_.Method -eq 'PUT' })
        $expected = @($global:EcrTest.Manifest.fields | Where-Object { $_.kind -ne 'Service' }).Count
        $puts | Should -HaveCount $expected
        foreach ($p in $puts) { $p.Path | Should -BeLike '/api/v1/template-versions/2/header-fields/*' }
        @($writes | Where-Object { $_.Path -eq '/api/v1/template-versions/2/publish' }) | Should -HaveCount 1
    }
}

Describe 'Apply-ContractHeader.ps1: міграція документів тільки на останню опубліковану версію (N5-03)' {
    It '0 змін і -SourceVersionId старіший за останню опубліковану: помилка, міграції немає' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true },
            @{ id = 2; version = '1.0.1.0'; status = 'Published'; match = $false }
        )
        { Invoke-Header @{ SourceVersionId = 1; MigrateDocumentId = @(3, 4) } } | Should -Throw -ExpectedMessage '*не остання опублікована*'
        @($global:FakeEcr.Calls | Where-Object { $_.Path -like '*/migrate-version' }) | Should -HaveCount 0
        Get-FakeWrites | Should -HaveCount 0
    }

    It '0 змін, основа = остання опублікована: документи йдуть саме на неї (dry-run, потім застосування)' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true },
            @{ id = 2; version = '1.0.1.0'; status = 'Published'; match = $true }
        )
        Invoke-Header @{ MigrateDocumentId = @(3) }

        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -HaveCount 2
        foreach ($m in $mig) { $m.Body.targetVersionId | Should -Be 2 }
        foreach ($w in (Get-FakeWrites)) { $w.Path | Should -BeLike '*/migrate-version' }
    }

    It 'зміни є: клон останньої опублікованої, публікація, міграція на НОВУ версію' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false }
        )
        Invoke-Header @{ MigrateDocumentId = @(3) }

        @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/template-versions/1/clone' }) | Should -HaveCount 1
        @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/template-versions/50/publish' }) | Should -HaveCount 1
        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -Not -BeNullOrEmpty
        foreach ($m in $mig) { $m.Body.targetVersionId | Should -Be 50 }
    }

    It 'невідомий -SourceVersionId — помилка' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        { Invoke-Header @{ SourceVersionId = 99 } } | Should -Throw -ExpectedMessage '*Версії 99*'
        Get-FakeWrites | Should -HaveCount 0
    }
}
