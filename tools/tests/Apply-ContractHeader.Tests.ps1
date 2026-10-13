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
            # R16-2: документ уже на цільовій версії — сервер відмовляє 422 migrateSameVersion (і на dry-run).
            if ($s.ContainsKey('SameVersionDocs') -and @($s.SameVersionDocs) -contains [int]$Matches[1]) {
                $r = New-FakeResponse 422 @{ code = 'ECR-TMPL-0422' }
                $r.Text = '{"code":"ECR-TMPL-0422","details":{"messageKey":"err.ECR-TMPL-0422.migrateSameVersion"}}'
                return $r
            }
            if ($Body.dryRun) { return New-FakeResponse 200 @{ canApply = $true; refusals = @() } }
            # LP-43: async (не сухий прогін) → 202 + jobId, як у DocumentsController.MigrateVersion.
            if ($Body.ContainsKey('async') -and $Body.async) { return New-FakeResponse 202 @{ jobId = 'job-mig-1'; documentId = [int]$Matches[1] } }
            return New-FakeResponse 200 @{ transferredValues = 0; lostValues = 0 }
        }
        if ($Method -eq 'GET' -and $Path -eq '/api/v1/jobs/job-mig-1') {
            return New-FakeResponse 200 @{ state = 'Succeeded'; percent = 100; message = 'ok' }
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
            # Документи, на яких migrate-version відмовляє 422 migrateSameVersion; без ключа StrictMode кидає
            # PropertyNotFoundException на читанні $s.SameVersionDocs у Invoke-FakeEcr.
            SameVersionDocs = @()
        }
    }

    function global:Invoke-Header {
        param([hashtable]$Extra = @{})
        # guard builder-fixes-r2: мутація (не -DryRun) потребує -ConfirmStand = адреса стенду (той самий BaseUrl).
        # Тест може передати власне значення (зокрема порожнє — перевірка відмови); guard у скрипті не послаблюється.
        $baseUrl = 'http://localhost:5092'
        $args2 = @{ TemplateCode = 'Land'; DataDir = $global:EcrTest.DataDir; BaseUrl = $baseUrl }
        if (-not $Extra.ContainsKey('DryRun') -and -not $Extra.ContainsKey('ConfirmStand')) { $args2['ConfirmStand'] = $baseUrl }
        foreach ($k in $Extra.Keys) { $args2[$k] = $Extra[$k] }
        & $global:EcrTest.ScriptPath @args2 *> $null
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

Describe 'Apply-ContractHeader.ps1: guard -ConfirmStand (builder-fixes-r2)' {
    It 'без -ConfirmStand мутуючий виклик зупиняється і нічого не записує' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false }
        )
        { Invoke-Header @{ ConfirmStand = '' } } | Should -Throw -ExpectedMessage '*-ConfirmStand*'
        $global:FakeEcr.Calls | Should -HaveCount 0
        Get-FakeWrites | Should -HaveCount 0
    }

    It 'чужий -ConfirmStand (інший порт) теж відмовляє і нічого не записує' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false }
        )
        { Invoke-Header @{ ConfirmStand = 'localhost:9999' } } | Should -Throw -ExpectedMessage '*не збігається*'
        Get-FakeWrites | Should -HaveCount 0
    }

    It '-DryRun не вимагає -ConfirmStand і лише читає' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $false }
        )
        { Invoke-Header @{ DryRun = $true } } | Should -Not -Throw
        Get-FakeWrites | Should -HaveCount 0
    }
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

    It 'R16-2: документ уже на цільовій версії (422 migrateSameVersion) — «0 змін», без помилки, інші документи йдуть далі' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        $global:FakeEcr.SameVersionDocs = @(3)
        { Invoke-Header @{ MigrateDocumentId = @(3, 10) } } | Should -Not -Throw
        $applies = @($global:FakeEcr.Calls | Where-Object { $_.Path -like '*/migrate-version' -and -not $_.Body.dryRun })
        $applies | Should -HaveCount 1
        $applies[0].Path | Should -Be '/api/v1/documents/10/migrate-version'
    }

    It '-TableRenameMap: мапа перейменувань іде в тіло dry-run і застосування migrate-version' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        $map = Join-Path $TestDrive 'renames.json'
        Set-Content -LiteralPath $map -Encoding UTF8 -Value '[{"fromTableCode":"T9a_03","toTableCode":"T9a_17a"}]'
        Invoke-Header @{ MigrateDocumentId = @(3); TableRenameMap = $map }

        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -HaveCount 2
        foreach ($m in $mig) {
            @($m.Body.tableRenames) | Should -HaveCount 1
            $m.Body.tableRenames[0].fromTableCode | Should -Be 'T9a_03'
            $m.Body.tableRenames[0].toTableCode | Should -Be 'T9a_17a'
        }
    }

    It 'без -TableRenameMap тіло migrate-version не містить tableRenames (поведінка не змінилась)' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        Invoke-Header @{ MigrateDocumentId = @(3) }

        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -HaveCount 2
        foreach ($m in $mig) { $m.Body.ContainsKey('tableRenames') | Should -BeFalse }
    }

    It 'LP-43: застосування міграції за замовчуванням у фоні (async: true, 202, опитування jobs); dry-run без async' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        { Invoke-Header @{ MigrateDocumentId = @(3) } } | Should -Not -Throw

        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -HaveCount 2
        $mig[0].Body.dryRun | Should -BeTrue
        $mig[0].Body.ContainsKey('async') | Should -BeFalse
        $mig[1].Body.dryRun | Should -BeFalse
        $mig[1].Body.async | Should -BeTrue
        @($global:FakeEcr.Calls | Where-Object { $_.Method -eq 'GET' -and $_.Path -eq '/api/v1/jobs/job-mig-1' }) | Should -HaveCount 1
    }

    It 'LP-43: -Synchronous — застосування без async, без опитування jobs' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        Invoke-Header @{ MigrateDocumentId = @(3); Synchronous = $true }

        $mig = @($global:FakeEcr.Calls | Where-Object { $_.Path -eq '/api/v1/documents/3/migrate-version' })
        $mig | Should -HaveCount 2
        foreach ($m in $mig) { $m.Body.ContainsKey('async') | Should -BeFalse }
        @($global:FakeEcr.Calls | Where-Object { $_.Path -like '/api/v1/jobs/*' }) | Should -HaveCount 0
    }

    It 'готова мапа tools/land/rename-maps/land-9a-missing-tables.json описує три таблиці 9a' {
        $path = Join-Path (Join-Path $PSScriptRoot '..') 'land/rename-maps/land-9a-missing-tables.json'
        $map = @(Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
        ($map | ForEach-Object { "$($_.fromTableCode)>$($_.toTableCode)" }) | Should -Be @('T9a_03>T9a_17a', 'T9a_06>T9a_21a', 'T9a_07>T9a_21b')
    }
    It 'R16-3: «3,10» одним рядком (powershell -File) — два документи, а не 310' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        Invoke-Header @{ MigrateDocumentId = '3,10' }
        $paths = @($global:FakeEcr.Calls | Where-Object { $_.Path -like '*/migrate-version' } | ForEach-Object { $_.Path } | Select-Object -Unique)
        $paths | Should -Be @('/api/v1/documents/3/migrate-version', '/api/v1/documents/10/migrate-version')
    }

    It 'невідомий -SourceVersionId — помилка' {
        Initialize-FakeEcr @(
            @{ id = 1; version = '1.0.0.0'; status = 'Published'; match = $true }
        )
        { Invoke-Header @{ SourceVersionId = 99 } } | Should -Throw -ExpectedMessage '*Версії 99*'
        Get-FakeWrites | Should -HaveCount 0
    }
}
