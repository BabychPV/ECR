<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/Build-LandTemplate.ps1 -BuildUser — обліковий запис етапів
    збірки (за замовчуванням land-owner) доходить до КОЖНОГО LoginAs і до Import-ContractDictionaries / Apply-ContractHeader;
    етап Accounts і далі створює land-owner. Мережі немає: функції беруться з AST, Connect-Ecr і підскрипти підмінено.
#>

Describe 'Build-LandTemplate: -BuildUser' {
    It 'параметр -BuildUser має типове значення land-owner; літерал land-owner лишився лише в переліку акаунтів етапу Accounts' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $path = Join-Path $repo 'tools/land/Build-LandTemplate.ps1'
        $tokens = $null; $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errs)
        $p = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'BuildUser' }
        if ($null -eq $p -or $p.DefaultValue.Value -ne 'land-owner') { throw 'немає -BuildUser з типовим land-owner' }
        $lits = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $n.Value -eq 'land-owner' }, $true))
        $outside = @($lits | Where-Object {
                $n = $_.Parent; $inTable = $false
                while ($null -ne $n) { if ($n -is [System.Management.Automation.Language.HashtableAst]) { $inTable = $true; break }; $n = $n.Parent }
                -not ($_.Parent -is [System.Management.Automation.Language.ParameterAst]) -and -not $inTable })
        if ($outside.Count -ne 0) { throw ("літерал land-owner поза переліком акаунтів: рядки " + (($outside | ForEach-Object { $_.Extent.StartLineNumber }) -join ', ')) }
    }

    It 'кожен LoginAs і обидва підскрипти отримують $BuildUser (AST), пароль - $Acc[$BuildUser]' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $calls = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'LoginAs' }, $true))
        if ($calls.Count -lt 20) { throw "LoginAs викликів $($calls.Count) (очікувалось >= 20)" }
        # bootstrap / land-reviewer / land-approver / land-entry - інші ролі, лишаються; land-owner - лише через $BuildUser
        $bad = @($calls | Where-Object { $_.CommandElements[1].Extent.Text -eq "'land-owner'" -or $_.CommandElements[1].Extent.Text -eq '"land-owner"' })
        if ($bad.Count) { throw ('LoginAs land-owner літералом: рядки ' + (($bad | ForEach-Object { $_.Extent.StartLineNumber }) -join ', ')) }
        $mine = @($calls | Where-Object { $_.CommandElements[1].Extent.Text -eq '$BuildUser' })
        if ($mine.Count -ne 23) { throw "LoginAs `$BuildUser: $($mine.Count), очікувалось 23 (усі колишні land-owner + Stage-BranchGate + Stage-RequiredInputs)" }
        $text = [IO.File]::ReadAllText((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'))
        foreach ($s in 'Import-ContractDictionaries.ps1', 'Apply-ContractHeader.ps1') {
            $line = ($text -split "`n") | Where-Object { $_ -like "*$s'*" -and $_ -like '*-User*' }
            if (@($line).Count -ne 1 -or $line -notlike '*-User $BuildUser*') { throw "$s без -User `$BuildUser" }
        }
        if (([regex]::Matches($text, [regex]::Escape('$Acc[$BuildUser]'))).Count -lt 2) { throw 'пароль підскриптів не за ключем $BuildUser' }
    }

    It 'виконання: LoginAs і Stage-Contract з -BuildUser ходять під цим записом; типове значення - land-owner' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        foreach ($fn in 'LoginAs', 'Stage-Contract') {
            $def = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fn }, $true)
            . ([scriptblock]::Create($def.Extent.Text))
        }
        function Connect-Ecr { param($BaseUrl, $User, $Password) "LOGIN=$User" }
        function Log($m) { }
        $here = Join-Path ([IO.Path]::GetTempPath()) ("ecr-builduser-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $here | Out-Null
        $seen = @()
        try {
            Set-Content -Path (Join-Path $here 'Import-ContractDictionaries.ps1') -Encoding ascii -Value 'param($BaseUrl, $User, $Password, $ConfirmStand) "IMPORT=$User"'
            $Acc = @{ 'land-owner' = 'p1'; 'au-runner' = 'p2' }
            $BaseUrl = 'http://localhost:1'; $childStand = 'localhost:1'
            $BuildUser = 'land-owner'                       # типове значення параметра
            $seen += @(LoginAs $BuildUser) + @(Stage-Contract)
            $BuildUser = 'au-runner'                        # -BuildUser au-runner
            $seen += @(LoginAs $BuildUser) + @(Stage-Contract)
        }
        finally { Remove-Item -Recurse -Force $here }
        $got = $seen -join ','
        if ($got -ne 'LOGIN=land-owner,IMPORT=land-owner,LOGIN=au-runner,IMPORT=au-runner') { throw "отримано: $got" }
    }

    It 'B98: -Stage Template без запису -BuildUser у -AccountsFile - відмова ДО мережі з іменами параметрів, без пароля' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $def = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-BuildUserCredentialGap' }, $true)
        if ($null -eq $def) { throw 'немає Get-BuildUserCredentialGap: етапи без Accounts падали б на першому LoginAs посеред етапу' }
        . ([scriptblock]::Create($def.Extent.Text))
        $created = @('land-owner', 'land-reviewer')
        # файла немає, етап Template, постійний запис -> відмова з -BuildUser/-AccountsFile і порадою, без створення
        $m = Get-BuildUserCredentialGap -Stages @('Template') -User 'au-runner' -Accounts @{} -AccountsPath 'X:\none.txt' -FileExists $false -CreatedUsers $created
        foreach ($need in "-BuildUser 'au-runner'", "-AccountsFile 'X:\none.txt'", 'немає', 'НАЯВНИЙ', 'не створює') {
            if ([string]$m -notlike "*$need*") { throw "у відмові немає «$need»: $m" }
        }
        # файл є, рядка немає; пароль чужого запису в тексті не з'являється
        $m = Get-BuildUserCredentialGap -Stages @('Template', 'Project') -User 'au-runner' -Accounts @{ 'land-owner' = 'S3cret-pw' } -AccountsPath 'a.txt' -FileExists $true -CreatedUsers $created
        if ([string]$m -notlike "*немає рядка для 'au-runner'*" -or [string]$m -like '*S3cret-pw*') { throw "файл без рядка: $m" }
        # Accounts у прогоні створить лише land-*: чужий -BuildUser однаково відмова
        $m = Get-BuildUserCredentialGap -Stages @('All') -User 'au-runner' -Accounts @{} -AccountsPath 'a.txt' -FileExists $false -CreatedUsers $created
        if ([string]$m -notlike '*етап Accounts створює лише*') { throw "All + чужий запис: $m" }
        # без відмови: запис у файлі; Accounts створить land-owner; лише Accounts/YearMonths (без входу під -BuildUser)
        $ok = @(
            (Get-BuildUserCredentialGap -Stages @('Template') -User 'au-runner' -Accounts @{ 'au-runner' = 'x' } -AccountsPath 'a.txt' -FileExists $true -CreatedUsers $created),
            (Get-BuildUserCredentialGap -Stages @('All') -User 'land-owner' -Accounts @{} -AccountsPath 'a.txt' -FileExists $false -CreatedUsers $created),
            (Get-BuildUserCredentialGap -Stages @('Accounts', 'Units') -User 'land-owner' -Accounts @{} -AccountsPath 'a.txt' -FileExists $false -CreatedUsers $created),
            (Get-BuildUserCredentialGap -Stages @('Accounts') -User 'au-runner' -Accounts @{} -AccountsPath 'a.txt' -FileExists $false -CreatedUsers $created),
            (Get-BuildUserCredentialGap -Stages @('YearMonths') -User 'au-runner' -Accounts @{} -AccountsPath 'a.txt' -FileExists $false -CreatedUsers $created))
        if (@($ok | Where-Object { $_ }).Count -ne 0) { throw ('зайва відмова: ' + (@($ok | Where-Object { $_ }) -join ' | ')) }
        # виклик стоїть до розбору моделі й до будь-якого LoginAs; поза DryRun - throw
        $text = [IO.File]::ReadAllText((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'))
        $call = $text.IndexOf('$buildUserGap = Get-BuildUserCredentialGap')
        if ($call -lt 0 -or $call -gt $text.IndexOf('Log "Модель: $modelFile"')) { throw 'перевірка -BuildUser не стоїть до розбору моделі' }
        if ($text -notmatch 'if \(\$DryRun\) \{ Log "DryRun: WARN[^}]*\} else \{ throw \$buildUserGap \}') { throw 'поза DryRun перевірка не відмовляє' }
    }
}
