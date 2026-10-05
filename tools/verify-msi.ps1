<#
.SYNOPSIS
    Статичні перевірки складу MSI (без установки) і прогін сценаріїв 1, 5,
    9, 15 автоматично (Q-222: коментар раніше називав 4 і 6, яких тут
    ніколи не було), плюс сценарії воркера W1/W2 (ФВ-9.8, D-206). Решта —
    вручну: вони потребують перезавантаження або відсутності прав.
    Першими в будь-якому режимі — I1 (SHA-256 проти еталона) і I2 (підпис
    Authenticode): CL-3, MSI-SIGNING-OPTIONS (A+B). -IntegrityOnly — лише вони.
.PARAMETER StaticOnly
    Лише статичні перевірки таблиць MSI: нічого не встановлює, безпечно на
    будь-якій машині (зокрема на машині збірки).
.PARAMETER ExpectedSha256
    Еталонний SHA-256 пакета (64 hex), отриманий ОКРЕМИМ каналом (release
    notes, лист видавця). Без нього — -Sha256File, далі файл-супутник
    `<MsiPath>.sha256` (його пише build-msi.ps1 і переписує sign-msi.ps1).
    Еталона немає ніде — I1 падає: хеш перевіряється завжди (CL-3, MSI-SIGNING-OPTIONS (A+B)).
.PARAMETER Sha256File
    Файл з еталонним хешем: перше слово першого непорожнього рядка (формат
    `sha256sum`: `<hex>  Ecr.msi`).
.PARAMETER RequireSignature
    Підпис Authenticode обов'язковий: непідписаний або недовірений пакет — FAIL.
    Без ключа такий пакет — WARN (поки сертифіката немає, варіант B), але
    пошкоджений підпис (HashMismatch) — FAIL завжди.
.PARAMETER TrustedThumbprint
    Очікуваний відбиток сертифіката підписанта (варіант A — наш
    самопідписаний, C — корпоративний ЦС). Мається на увазі -RequireSignature.
.PARAMETER IntegrityOnly
    Лише I1 (SHA-256) і I2 (підпис), без таблиць MSI і без установки: перевірка
    адміністратора перед установкою (docs/admin/admin-guide.md §12).
.PARAMETER LogDir
    Куди класти журнали msiexec (/l*v). Типово — поточна тека (як і раніше).
    CI (джоба `msi-install (windows)`, tools/ci-msi-install.ps1) вивантажує
    їх артефактом при падінні.
.NOTES
    ⛔ Без -StaticOnly запускати ЛИШЕ на тестовій машині. Скрипт встановлює і
    видаляє службу. У CI — ефемерний ранер windows-latest.
    ⚠ Без -ServiceAccount служби реєструються під LocalSystem, але НЕ
    стартують (EcrServiceAutoStart/EcrWorkerAutoStart умовні на
    SERVICE_ACCOUNT) — саме так і в CI, де SQL для старту служб немає.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $PreviousMsiPath,
    [string] $ServiceAccount,
    [string] $LogDir,
    [switch] $StaticOnly,
    [string] $ExpectedSha256,
    [string] $Sha256File,
    [switch] $RequireSignature,
    [string] $TrustedThumbprint,
    [switch] $IntegrityOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$MsiPath = (Resolve-Path $MsiPath).Path

$results = [System.Collections.Generic.List[object]]::new()
function Test-Case([string] $name, [scriptblock] $body) {
    try { & $body; $results.Add([pscustomobject]@{ Case = $name; Result = 'PASS' }) }
    catch { $results.Add([pscustomobject]@{ Case = $name; Result = "FAIL: $_" }) }
}
# ⚠ Параметр НЕ `$args`: це автоматична змінна PowerShell, і оголошувати її
# параметром — пастка (до CI-прогону повний режим не запускався жодного разу).
function Invoke-Msi([string] $arguments) {
    $p = Start-Process msiexec -ArgumentList $arguments -Wait -PassThru
    if ($p.ExitCode -notin 0, 3010) {
        $log = if ($arguments -match '/l\*v\s+(\S+)') { " — журнал $($Matches[1])" } else { '' }
        throw "msiexec $arguments → $($p.ExitCode)$log"
    }
    $p.ExitCode
}

# ── Цілісність пакета (CL-3, MSI-SIGNING-OPTIONS (A+B), НФ-8.7): I1 — SHA-256, I2 — підпис ─────
# Йде ПЕРШОЮ: таблиці MSI пошкодженого пакета нема сенсу читати, а
# адміністратор на сервері (-IntegrityOnly) бачить лише ці два рядки.
# Рядок `[I1] FAIL: …` — стабільний формат, його читає tools/test-verify-msi.ps1.
function Write-Integrity([string] $case, [string] $verdict, [string] $message) {
    $line = "[$case] ${verdict}: $message"
    $color = @{ PASS = 'Green'; WARN = 'Yellow'; FAIL = 'Red' }[$verdict]
    Write-Host $line -ForegroundColor $color
    $results.Add([pscustomobject]@{ Case = "$case. $(@{ I1 = 'SHA-256'; I2 = 'Підпис Authenticode' }[$case])"; Result = "${verdict}: $message" })
}

function Get-ExpectedSha256 {
    if ($ExpectedSha256) { return @{ Hash = $ExpectedSha256.Trim(); Source = '-ExpectedSha256' } }
    $file = if ($Sha256File) { $Sha256File } elseif (Test-Path -LiteralPath "$MsiPath.sha256") { "$MsiPath.sha256" } else { $null }
    if (-not $file) { return $null }
    if (-not (Test-Path -LiteralPath $file)) { throw "файлу еталонного хешу немає: $file" }
    $first = Get-Content -LiteralPath $file | Where-Object { $_.Trim() } | Select-Object -First 1
    if (-not $first) { throw "файл еталонного хешу порожній: $file" }
    return @{ Hash = ($first.Trim() -split '\s+')[0].TrimStart('*'); Source = $file }
}

try {
    $reference = Get-ExpectedSha256
    if (-not $reference) {
        Write-Integrity I1 FAIL "еталонного хешу немає: передайте -ExpectedSha256 (з release notes) або -Sha256File; файла $MsiPath.sha256 поруч теж немає"
    }
    elseif ($reference.Hash -notmatch '^[0-9a-fA-F]{64}$') {
        Write-Integrity I1 FAIL "еталон '$($reference.Hash)' ($($reference.Source)) — не SHA-256 (очікували 64 hex-символи)"
    }
    else {
        $actual = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash
        # -ne для рядків у PowerShell нечутливе до регістру — hex у будь-якому регістрі.
        if ($actual -ne $reference.Hash) {
            Write-Integrity I1 FAIL "SHA-256 пакета $($actual.ToLower()) ≠ еталон $($reference.Hash.ToLower()) ($($reference.Source)) — файл пошкоджено або підмінено, НЕ встановлювати"
        }
        else {
            Write-Integrity I1 PASS "SHA-256 $($actual.ToLower()) збігається з еталоном ($($reference.Source))"
        }
    }
}
catch { Write-Integrity I1 FAIL "$_" }

$signatureRequired = $RequireSignature -or [bool] $TrustedThumbprint
$notOk = if ($signatureRequired) { 'FAIL' } else { 'WARN' }
try {
    if (-not (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
        # Не Windows (pwsh на Linux): перевірити підпис нічим.
        Write-Integrity I2 $notOk 'Get-AuthenticodeSignature недоступний на цій ОС — підпис не перевірено (перевіряйте на Windows)'
    }
    else {
        $sig = Get-AuthenticodeSignature -LiteralPath $MsiPath
        $signer = $sig.SignerCertificate
        $who = if ($signer) { "$($signer.Subject), відбиток $($signer.Thumbprint)" } else { '' }
        switch ([string] $sig.Status) {
            'Valid' {
                if ($TrustedThumbprint -and $signer.Thumbprint -ne ($TrustedThumbprint -replace '\s', '')) {
                    Write-Integrity I2 FAIL "підпис дійсний, але підписант не той: $who; очікували відбиток $TrustedThumbprint"
                }
                else { Write-Integrity I2 PASS "підпис дійсний і довірений: $who" }
            }
            'HashMismatch' {
                # Підпис є, а вміст після підпису змінено — це підміна або
                # пошкодження, хоч би що казав хеш (його могли переписати разом).
                Write-Integrity I2 FAIL "HashMismatch — вміст змінено після підпису ($who), НЕ встановлювати"
            }
            'NotSigned' {
                Write-Integrity I2 $notOk 'пакет не підписано: Windows покаже «невідомий видавець»; цілісність тримає лише I1'
            }
            default {
                # NotTrusted / UnknownError (недовірений корінь самопідписаного
                # сертифіката — .cer не встановлено на цьому сервері) / Incompatible.
                Write-Integrity I2 $notOk "підпис не довірений на цій машині ($($sig.Status): $($sig.StatusMessage)) $who"
            }
        }
    }
}
catch { Write-Integrity I2 FAIL "$_" }

if ($IntegrityOnly) {
    $results | Format-Table -AutoSize -Wrap
    if ($results.Result -match '^FAIL') { exit 1 }
    return
}
# SERVICE_ACCOUNT= з порожнім значенням не передаємо взагалі: порожня
# властивість у командному рядку msiexec — зайвий ризик 1639, а сенс той самий.
$acct = if ($ServiceAccount) { " SERVICE_ACCOUNT=$ServiceAccount" } else { '' }

# StartMode з WMI (Auto/Manual/Disabled) + DelayedAutoStart з реєстру: Get-Service
# у різних версіях PowerShell показує відкладений старт по-різному.
function Assert-AutoStart([string] $name) {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if (-not $svc) { throw "служби $name немає" }
    if ($svc.StartMode -ne 'Auto') { throw "$name StartMode = $($svc.StartMode), очікували Auto" }
    $delayed = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$name" -Name DelayedAutoStart -ErrorAction SilentlyContinue
    if (-not $delayed -or $delayed.DelayedAutoStart -ne 1) { throw "$name без DelayedAutoStart = 1" }
    return $svc
}

# ⛔ L10-02: тека config — захищений DACL (без успадкування від %ProgramData%,
# де BUILTIN\Users можуть створювати файли). Писати в неї й у файл конфігу
# можуть лише SYSTEM і Administrators; Users — лише читати.
function Assert-NoForeignWrite([string] $path) {
    $acl = Get-Acl -LiteralPath $path
    # WriteData/CreateFiles, AppendData, WriteExtendedAttributes,
    # DeleteSubdirectoriesAndFiles, WriteAttributes, Delete, WRITE_DAC,
    # WRITE_OWNER, GENERIC_ALL, GENERIC_WRITE.
    $writeMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000
    $trusted = 'S-1-5-18', 'S-1-5-32-544'   # SYSTEM, BUILTIN\Administrators
    foreach ($rule in $acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        if ($trusted -contains $rule.IdentityReference.Value) { continue }
        if (([int64] $rule.FileSystemRights -band $writeMask) -ne 0) {
            throw "$path : $($rule.IdentityReference.Translate([System.Security.Principal.NTAccount])) має право запису ($($rule.FileSystemRights))"
        }
    }
    return $acl
}
function Assert-ConfigFolderProtected {
    $dir = Join-Path $env:ProgramData 'ECR\config'
    $acl = Assert-NoForeignWrite $dir
    if (-not $acl.AreAccessRulesProtected) { throw "$dir успадковує права %ProgramData% (DACL не захищений)" }
    $file = Join-Path $dir 'appsettings.Production.json'
    if (Test-Path -LiteralPath $file) { Assert-NoForeignWrite $file | Out-Null }
}

# ── Статичні перевірки: таблиці MSI, без установки ────────────────────────
$installer = New-Object -ComObject WindowsInstaller.Installer
$db = $installer.OpenDatabase($MsiPath, 0)   # 0 = лише читання

# Рядки запиту як масиви рядків (стовпці з 1). Порожній результат — порожній масив.
function Get-MsiRows([string] $query, [int] $columns) {
    # ⚠ [void] обов'язковий: COM-методи Execute/Close повертають VT_EMPTY, і
    # PowerShell видає його у вихід функції як $null — рядків ставало більше
    # на два (перевірено на синтетичному MSI).
    $view = $db.OpenView($query)
    [void] $view.Execute()
    $rows = @()
    while ($record = $view.Fetch()) {
        $rows += , @(1..$columns | ForEach-Object { $record.StringData($_) })
    }
    [void] $view.Close()
    return , $rows
}

# FileName у таблиці File — «КОРОТКЕ|Довге» або лише ім'я.
function Find-MsiFile([string] $longName) {
    $all = Get-MsiRows 'SELECT `File`, `Component_`, `FileName` FROM `File`' 3
    return , @($all | Where-Object { ($_[2] -split '\|')[-1] -eq $longName })
}

Test-Case 'S1. Ecr.Worker.exe і worker.settings.json у MSI' {
    foreach ($name in 'Ecr.Api.exe', 'Ecr.Worker.exe', 'Ecr.Worker.dll', 'worker.settings.json') {
        $found = Find-MsiFile $name
        if ($found.Count -ne 1) { throw "$name у таблиці File: $($found.Count) рядків, очікували 1" }
    }
}

# I2-2 (2026-09-30): типово 1 — служба є на свіжій установці й на оновленні без
# властивостей; інакше оновлення мовчки знімало б виконавця перерахунку.
Test-Case 'S2. WORKER_ENABLED типово 1' {
    $rows = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'WORKER_ENABLED'" 1
    if ($rows.Count -ne 1 -or $rows[0][0] -ne '1') { throw "WORKER_ENABLED = '$($rows | ForEach-Object { $_[0] })', очікували '1'" }
    $secure = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = 'SecureCustomProperties'" 1
    if ($secure.Count -ne 1 -or ($secure[0][0] -split ';') -notcontains 'WORKER_ENABLED') {
        throw 'WORKER_ENABLED не в SecureCustomProperties — з /qn під UAC значення не дійде до серверної частини'
    }
}

Test-Case 'S3. Служба EcrWorker умовна, --supervisor, бінарник — Ecr.Worker.exe' {
    $svc = Get-MsiRows "SELECT ``Component_``, ``Arguments``, ``StartName`` FROM ``ServiceInstall`` WHERE ``Name`` = 'EcrWorker'" 3
    if ($svc.Count -ne 1) { throw "ServiceInstall EcrWorker: $($svc.Count) рядків" }
    $component = $svc[0][0]
    if ($svc[0][1] -ne '--supervisor') { throw "Arguments = '$($svc[0][1])'" }
    if ($svc[0][2] -ne '[SERVICE_ACCOUNT]') { throw "StartName = '$($svc[0][2])', очікували той самий обліковий запис, що EcrApi" }

    $comp = Get-MsiRows "SELECT ``Condition``, ``Attributes``, ``KeyPath`` FROM ``Component`` WHERE ``Component`` = '$component'" 3
    if ($comp[0][0] -notmatch 'WORKER_ENABLED\s*=\s*"1"') { throw "умова компонента '$($comp[0][0])' — служба ставилася б завжди" }
    if (([int] $comp[0][1] -band 0x40) -eq 0) { throw 'компонент не Transitive — REINSTALL з WORKER_ENABLED=0/1 не перемикатиме службу' }

    # Бінарник служби = ключовий файл компонента.
    $exe = Find-MsiFile 'Ecr.Worker.exe'
    if ($comp[0][2] -ne $exe[0][0]) { throw "KeyPath компонента '$($comp[0][2])' — не Ecr.Worker.exe ('$($exe[0][0])')" }
}

Test-Case 'S4. Старт EcrWorker — лише за WORKER_ENABLED=1 і SERVICE_ACCOUNT' {
    # Event: 0x1 = Start при установці.
    $ctl = Get-MsiRows "SELECT ``Component_``, ``Event`` FROM ``ServiceControl`` WHERE ``Name`` = 'EcrWorker'" 2
    $starts = @($ctl | Where-Object { ([int] $_[1] -band 0x1) -ne 0 })
    if ($starts.Count -ne 1) { throw "ServiceControl зі стартом EcrWorker: $($starts.Count)" }
    $cond = (Get-MsiRows "SELECT ``Condition`` FROM ``Component`` WHERE ``Component`` = '$($starts[0][0])'" 1)[0][0]
    if ($cond -notmatch 'WORKER_ENABLED' -or $cond -notmatch 'SERVICE_ACCOUNT') { throw "умова старту '$cond'" }
}

Test-Case 'S5. EcrApi — безумовна, як і раніше' {
    $svc = Get-MsiRows "SELECT ``Component_`` FROM ``ServiceInstall`` WHERE ``Name`` = 'EcrApi'" 1
    if ($svc.Count -ne 1) { throw "ServiceInstall EcrApi: $($svc.Count) рядків" }
    $cond = (Get-MsiRows "SELECT ``Condition`` FROM ``Component`` WHERE ``Component`` = '$($svc[0][0])'" 1)[0][0]
    if ($cond) { throw "компонент EcrApi отримав умову '$cond'" }
}

function Get-MsiProperty([string] $name) {
    $rows = Get-MsiRows "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$name'" 1
    if ($rows.Count -ne 1) { return $null }
    return $rows[0][0]
}
$currentVersion = Get-MsiProperty 'ProductVersion'
$upgradeCode = Get-MsiProperty 'UpgradeCode'

$db = $null
[System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer) | Out-Null

if ($StaticOnly) {
    $results | Format-Table -AutoSize
    if ($results.Result -match '^FAIL') { exit 1 }
    return
}

# ── Сценарії з установкою (лише тестова машина) ───────────────────────────
if ($env:COMPUTERNAME -eq 'PROD-SERVER') { throw "не запускати на продуктиві" }

if ($LogDir) {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Set-Location $LogDir
}

# Встановлені продукти з тим самим UpgradeCode → версії. Більше одного —
# оновлення поставило продукт ПОРУЧ, а не замість (MajorUpgrade не спрацював).
function Get-InstalledEcrVersions {
    $msi = New-Object -ComObject WindowsInstaller.Installer
    try {
        return , @(foreach ($code in $msi.RelatedProducts($upgradeCode)) { $msi.ProductInfo($code, 'VersionString') })
    }
    finally { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($msi) | Out-Null }
}

Test-Case '1. Чиста установка' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c1.log$acct"
    Assert-AutoStart 'EcrApi' | Out-Null
    # I2-2: без WORKER_ENABLED служба воркера є — типове значення 1.
    if (-not (Get-CimInstance Win32_Service -Filter "Name='EcrWorker'")) { throw 'EcrWorker не зареєстровано без WORKER_ENABLED (типове 1)' }
    $w = Assert-AutoStart 'EcrWorker'
    if ($w.PathName -notmatch 'Ecr\.Worker\.exe"?\s+--supervisor') { throw "PathName = $($w.PathName)" }
    $api = Get-CimInstance Win32_Service -Filter "Name='EcrApi'"
    if ($w.StartName -ne $api.StartName) { throw "обліковий запис EcrWorker '$($w.StartName)' ≠ EcrApi '$($api.StartName)'" }
    # Без облікового запису служби не стартують (§1.4) — і воркер теж.
    if (-not $ServiceAccount) {
        foreach ($s in $api, $w) { if ($s.State -ne 'Stopped') { throw "$($s.Name) у стані $($s.State) без SERVICE_ACCOUNT, очікували Stopped" } }
    }
    $exe = ($w.PathName -replace '^"([^"]+)".*$', '$1')
    if (-not (Test-Path $exe)) { throw "бінарника служби немає на диску: $exe" }
    Assert-ConfigFolderProtected
}

Test-Case 'W1. WORKER_ENABLED=0 прибирає службу, EcrApi лишається (REINSTALL, транзитивний компонент)' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw1.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=0$acct"
    if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw 'EcrWorker лишився' }
    Get-Service EcrApi -ErrorAction Stop | Out-Null
}

Test-Case 'W2. Той самий MSI, WORKER_ENABLED=1 повертає службу' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw2.log REINSTALL=ALL REINSTALLMODE=vomus WORKER_ENABLED=1$acct"
    if (-not (Get-Service EcrWorker -ErrorAction SilentlyContinue)) { throw 'EcrWorker не зареєстровано' }
    Assert-AutoStart 'EcrWorker' | Out-Null
}

# I2-2: оновлення з попередньої версії БЕЗ властивостей — служба воркера є
# (попередня версія могла ставити без неї: тоді це саме той випадок, де
# Executor = Worker лишився б без виконавця, якби типове було 0).
if ($PreviousMsiPath) {
    $previous = (Resolve-Path $PreviousMsiPath).Path
    Test-Case 'W3. Оновлення з попередньої версії без WORKER_ENABLED — служба є' {
        Invoke-Msi "/x `"$MsiPath`" /qn /l*v cw3x.log"
        Invoke-Msi "/i `"$previous`" /qn /l*v cw3a.log WORKER_ENABLED=0$acct"
        Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw3b.log$acct"
        if (-not (Get-Service EcrWorker -ErrorAction SilentlyContinue)) { throw 'після оновлення EcrWorker немає' }
    }

    # Звичайний шлях адміністратора: попередня стоїть із типовими
    # властивостями, оновлення поточною — служба воркера ЛИШАЄТЬСЯ, і стоїть
    # рівно одна версія продукту (MajorUpgrade прибрав попередню).
    Test-Case 'W3b. Попередня (типові властивості) → оновлення поточною: EcrWorker лишився' {
        Invoke-Msi "/x `"$MsiPath`" /qn /l*v cw3cx.log"
        Invoke-Msi "/i `"$previous`" /qn /l*v cw3ca.log$acct"
        $before = if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { 'є' } else { 'немає' }
        Write-Host "  W3b: після попередньої MSI служба EcrWorker — $before; версії: $((Get-InstalledEcrVersions) -join ', ')"
        Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw3cb.log$acct"
        Assert-AutoStart 'EcrWorker' | Out-Null
        Assert-AutoStart 'EcrApi' | Out-Null
        $versions = Get-InstalledEcrVersions
        if ($versions.Count -ne 1 -or $versions[0] -ne $currentVersion) {
            throw "після оновлення встановлено версії [$($versions -join ', ')], очікували лише $currentVersion"
        }
        # L10-02: наявна тека з успадкованими правами (поставила попередня MSI)
        # після оновлення теж захищена.
        Assert-ConfigFolderProtected
    }
}

Test-Case '5. Та сама версія поверх' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c5.log"
}

Test-Case '15. Пароль не в лозі' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v c15.log SERVICE_PASSWORD=Sentinel-Not-In-Log"
    if (Select-String -Path c15.log -Pattern 'Sentinel-Not-In-Log' -Quiet) {
        throw "пароль знайдено в лозі — MsiHiddenProperties не працює"
    }
}

Test-Case '9. Видалення' {
    Invoke-Msi "/x `"$MsiPath`" /qn /l*v c9.log"
    if (Get-Service EcrApi -ErrorAction SilentlyContinue) { throw "служба лишилася" }
    if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw "служба EcrWorker лишилася" }
    # Сам файл, а не лише тека: тека без файлу — це вже «конфіг прибрано».
    if (-not (Test-Path "$env:ProgramData\ECR\config\appsettings.Production.json")) { throw "конфіг прибрано, а не мав бути (appsettings.Production.json)" }
}

# Свіже встановлення з вимкненим воркером (deploy-ecr.ps1 -DisableWorker / Express
# передає саме WORKER_ENABLED=0): EcrApi є, служби EcrWorker і її exe — немає.
Test-Case 'W5. Чиста установка з WORKER_ENABLED=0 — служби EcrWorker немає' {
    Invoke-Msi "/i `"$MsiPath`" /qn /l*v cw5.log WORKER_ENABLED=0$acct"
    try {
        Assert-AutoStart 'EcrApi' | Out-Null
        if (Get-Service EcrWorker -ErrorAction SilentlyContinue) { throw 'EcrWorker зареєстровано за WORKER_ENABLED=0' }
        $api = Get-CimInstance Win32_Service -Filter "Name='EcrApi'"
        $dir = Split-Path ($api.PathName -replace '^"([^"]+)".*$', '$1') -Parent
        if (Test-Path (Join-Path $dir 'Ecr.Worker.exe')) { throw "Ecr.Worker.exe встановлено без служби ($dir)" }
    }
    finally { Invoke-Msi "/x `"$MsiPath`" /qn /l*v cw5x.log" | Out-Null }
}

$results | Format-Table -AutoSize
if ($results.Result -match '^FAIL') { exit 1 }
