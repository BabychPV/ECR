<#
.SYNOPSIS
    Тест перевірки цілісності MSI (CL-3, D-259): tools/verify-msi.ps1
    -IntegrityOnly і tools/sign-msi.ps1 -Thumbprint на справжньому Ecr.msi.
.DESCRIPTION
    Доводить не «зелений шлях», а відмови:
      T3/T4  — без еталона або з чужим еталоном I1 падає;
      T2/T6  — непідписаний/недовірений пакет за -RequireSignature/-TrustedThumbprint падає;
      T9     — пошкоджений підписаний файл: падають І хеш (I1), І підпис (I2);
      T10    — підміна вмісту з переписаним хешем: I1 проходить, I2 ловить.
    Сертифікат — тимчасовий самопідписаний (варіант A), довіра до нього
    ставиться в LocalMachine\Root і LocalMachine\TrustedPublisher і знімається
    у finally.
.NOTES
    ⛔ Змінює сховища сертифікатів машини — лише ефемерний ранер CI
    (GITHUB_ACTIONS=true, джоба `msi (windows)`) або тестова машина з
    -AllowCertStoreChanges.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [switch] $AllowCertStoreChanges
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowCertStoreChanges) {
    throw 'test-verify-msi.ps1 ставить тимчасовий сертифікат у LocalMachine\Root — лише ранер CI або -AllowCertStoreChanges на тестовій машині.'
}
if (-not (Get-Command Get-AuthenticodeSignature -ErrorAction SilentlyContinue)) {
    throw 'потрібен Windows: Get-AuthenticodeSignature недоступний'
}

$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$verify = Join-Path $PSScriptRoot 'verify-msi.ps1'
$sign = Join-Path $PSScriptRoot 'sign-msi.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("ecr-verify-msi-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$results = [System.Collections.Generic.List[object]]::new()

# Кожен випадок — в окремій теці: verify-msi.ps1 бере еталон із `<msi>.sha256` поруч.
function New-Case([string] $name, [string] $from) {
    $dir = Join-Path $work $name
    New-Item -ItemType Directory -Path $dir | Out-Null
    $dst = Join-Path $dir 'Ecr.msi'
    Copy-Item -LiteralPath $from -Destination $dst
    return $dst
}
function Write-Sidecar([string] $msi) {
    $hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLower()
    Set-Content -LiteralPath "$msi.sha256" -Value "$hash  Ecr.msi" -Encoding ascii
}
function Invoke-Verify([string] $msi, [string[]] $extra = @()) {
    $out = & pwsh -NoProfile -File $verify -MsiPath $msi -IntegrityOnly @extra 2>&1 | Out-String
    return [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}
# $expectCode: 0 — успіх, 1 — відмова; $lines — регулярні вирази, що мусять бути у виводі.
function Assert-Verify([string] $case, [pscustomobject] $run, [int] $expectCode, [string[]] $lines) {
    $problems = @()
    $ok = if ($expectCode -eq 0) { $run.Code -eq 0 } else { $run.Code -ne 0 }
    if (-not $ok) { $problems += "код $($run.Code), очікували $(if ($expectCode) { 'ненульовий' } else { '0' })" }
    foreach ($l in $lines) { if ($run.Out -notmatch $l) { $problems += "немає '$l'" } }
    if ($problems) {
        Write-Host "── $case — вивід verify-msi.ps1:`n$($run.Out)"
        $results.Add([pscustomobject]@{ Case = $case; Result = "FAIL: $($problems -join '; ')" })
    }
    else { $results.Add([pscustomobject]@{ Case = $case; Result = 'PASS' }) }
}
# Інвертує байти в трьох місцях (¼, ½, ¾ файла): більшу частину MSI займає
# вбудований cab, тож хоч одне влучає у вміст, який покриває підпис.
function Write-Corruption([string] $msi) {
    $fs = [IO.File]::Open($msi, 'Open', 'ReadWrite')
    try {
        foreach ($f in 0.25, 0.5, 0.75) {
            $pos = [long] ($fs.Length * $f)
            $fs.Position = $pos; $b = $fs.ReadByte()
            $fs.Position = $pos; $fs.WriteByte($b -bxor 0xFF)
        }
    }
    finally { $fs.Dispose() }
}
# Підміна вмісту через Windows Installer API: новий рядок у Property.
function Write-Tamper([string] $msi) {
    # ⚠ Дескриптор бази тримає файл, доки COM-об'єкти не звільнено явно:
    # `$db = $null` не досить — наступний Get-FileHash падав «file is being
    # used by another process» (перший прогін CI).
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $null; $view = $null
    try {
        $db = $installer.OpenDatabase($msi, 1)   # 1 = msiOpenDatabaseModeTransact
        $view = $db.OpenView("INSERT INTO ``Property`` (``Property``, ``Value``) VALUES ('EcrTamperProbe', '1')")
        [void] $view.Execute(); [void] $view.Close()
        [void] $db.Commit()
    }
    finally {
        foreach ($o in $view, $db, $installer) {
            if ($o) { [void] [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) }
        }
        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
    }
}
function Set-MachineTrust([System.Security.Cryptography.X509Certificates.X509Certificate2] $cert, [bool] $add) {
    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        try { if ($add) { $store.Add($cert) } else { $store.Remove($cert) } }
        finally { $store.Close() }
    }
}

$cert = $null
$trusted = $false
try {
    # ── Непідписаний пакет (сьогоднішній стан, варіант B) ─────────────────
    $unsigned = New-Case 'unsigned' $MsiPath
    Write-Sidecar $unsigned
    Assert-Verify 'T1. Непідписаний + вірний еталон → 0, I2 WARN' (Invoke-Verify $unsigned) 0 @('\[I1\] PASS', '\[I2\] WARN')
    Assert-Verify 'T2. Непідписаний, -RequireSignature → відмова' (Invoke-Verify $unsigned @('-RequireSignature')) 1 @('\[I1\] PASS', '\[I2\] FAIL')

    $noref = New-Case 'noref' $MsiPath
    Assert-Verify 'T3. Без еталона хешу → відмова' (Invoke-Verify $noref) 1 @('\[I1\] FAIL')
    Assert-Verify 'T4. Чужий еталон → відмова' (Invoke-Verify $unsigned @('-ExpectedSha256', ('ab' * 32))) 1 @('\[I1\] FAIL')

    # ── Самопідписаний сертифікат (варіант A) ────────────────────────────
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ECR CI Test Signing (CL-3)' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddDays(1)
    $thumb = $cert.Thumbprint
    # Живий X509Certificate2 зі сховища: якщо PKI підвантажився через сесію
    # сумісності Windows PowerShell, $cert вище — десеріалізована копія.
    $cert = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumb"

    $signed = New-Case 'signed' $MsiPath
    Write-Sidecar $signed
    $before = (Get-Content -LiteralPath "$signed.sha256")
    & pwsh -NoProfile -File $sign -MsiPath $signed -Thumbprint $thumb
    if ($LASTEXITCODE) { throw "sign-msi.ps1 -Thumbprint: код $LASTEXITCODE" }
    $after = (Get-Content -LiteralPath "$signed.sha256")
    $results.Add([pscustomobject]@{ Case = 'T5a. sign-msi.ps1 переписав .sha256 після підпису'
        Result = if ($before -ne $after -and $after -match "^[0-9a-f]{64}  Ecr\.msi$") { 'PASS' } else { "FAIL: до '$before', після '$after'" } })

    Assert-Verify 'T5. Підписаний, довіри немає, без вимоги → 0, I2 WARN' (Invoke-Verify $signed) 0 @('\[I1\] PASS', '\[I2\] WARN')
    Assert-Verify 'T6. Підписаний, довіри немає, -TrustedThumbprint → відмова' (Invoke-Verify $signed @('-TrustedThumbprint', $thumb)) 1 @('\[I2\] FAIL')

    # Довіра — як на сервері за варіантом A (.cer у Root і TrustedPublisher).
    Set-MachineTrust ([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)) $true
    $trusted = $true

    Assert-Verify 'T7. Підписаний і довірений, -TrustedThumbprint → 0, I1 і I2 PASS' (Invoke-Verify $signed @('-TrustedThumbprint', $thumb)) 0 @('\[I1\] PASS', '\[I2\] PASS')
    Assert-Verify 'T8. Чужий відбиток → відмова' (Invoke-Verify $signed @('-TrustedThumbprint', ('0' * 40))) 1 @('\[I2\] FAIL')

    $corrupt = New-Case 'corrupt' $signed
    Copy-Item -LiteralPath "$signed.sha256" -Destination "$corrupt.sha256"
    Write-Corruption $corrupt
    Assert-Verify 'T9. Пошкоджений підписаний файл → падають І хеш, І підпис' (Invoke-Verify $corrupt @('-TrustedThumbprint', $thumb)) 1 @('\[I1\] FAIL', '\[I2\] FAIL')

    $tampered = New-Case 'tampered' $signed
    Write-Tamper $tampered
    Write-Sidecar $tampered   # зловмисник переписав і хеш
    Assert-Verify 'T10. Підміна + переписаний хеш → I1 PASS, підпис ловить' (Invoke-Verify $tampered @('-TrustedThumbprint', $thumb)) 1 @('\[I1\] PASS', '\[I2\] FAIL')
}
finally {
    if ($cert) {
        if ($trusted) { Set-MachineTrust ([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)) $false }
        try { Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey -ErrorAction Stop }
        catch { Write-Warning "тимчасовий сертифікат не прибрано: $_" }
    }
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

$results | Format-Table -AutoSize -Wrap
if ($results.Result -match '^FAIL') { exit 1 }
