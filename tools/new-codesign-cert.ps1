<#
.SYNOPSIS
    Створює самопідписаний сертифікат підпису коду для Ecr.msi (варіант A, MSI-SIGNING-OPTIONS) і
    експортує його відкриту частину (.cer) для довіри через GPO або certutil.
.DESCRIPTION
    Рішення людини (квіз 06.11, Q-6): «A+B зараз, потім C» — підпис самопідписаним сертифікатом плюс
    SHA-256 у release notes; корпоративний ЦС (C) — пізніше.

    Запускати ОДИН раз на машині реліз-менеджера (на кожен перевипуск — знову). Скрипт:
      1. створює сертифікат типу CodeSigningCert (RSA 3072, SHA256) у сховищі `Cert:\<Store>\My`;
         закритий ключ типово НЕЕКСПОРТОВАНИЙ — він живе лише на цій машині;
      2. експортує відкриту частину в `-CerPath` (лише сертифікат, без ключа) — її розповсюджують;
      3. з `-BackupPfxPath` (і лише тоді ключ створюється експортованим) — резервна копія PFX під
         паролем `-PfxPassword` (SecureString; ніколи не аргументом-рядком і не у вивід);
      4. друкує відбиток і наступні кроки: `sign-msi.ps1 -Thumbprint`, `verify-msi.ps1`, certutil/GPO.

    Нічого не підписує і нічого не встановлює в довірені сховища цієї машини.
.PARAMETER CerPath
    Куди записати відкриту частину (.cer). Файл не перезаписується.
.PARAMETER Subject
    Ім'я підписанта. Нейтральний дефолт — 'CN=ECR Code Signing'; реальне ім'я організації — факт
    замовника, не скрипта.
.PARAMETER Years
    Строк дії (типово 2). Після нього — перевипуск і повторне розповсюдження .cer.
.PARAMETER Store
    CurrentUser (типово, ключ доступний лише цьому користувачу) або LocalMachine.
.PARAMETER BackupPfxPath
    Необов'язкова резервна копія з ключем. Без неї ключ неекспортований і втрачається разом із
    машиною (тоді — новий сертифікат і новий .cer). Копію зберігати офлайн, окремо від пароля.
.PARAMETER PfxPassword
    Пароль резервної копії (обов'язковий разом із -BackupPfxPath).
.EXAMPLE
    .\tools\new-codesign-cert.ps1 -CerPath .\EcrCodeSigning.cer -WhatIf
.NOTES
    Лише Windows (модуль PKI). Перевірка підпису — tools/verify-msi.ps1 -IntegrityOnly -TrustedThumbprint.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $CerPath,
    [string] $Subject = 'CN=ECR Code Signing',
    [ValidateRange(1, 5)] [int] $Years = 2,
    [ValidateSet('CurrentUser', 'LocalMachine')] [string] $Store = 'CurrentUser',
    [string] $BackupPfxPath,
    [securestring] $PfxPassword
)
$ErrorActionPreference = 'Stop'

# ⛔ F13-p189 (P3): резервна копія ключа НЕ лягає в робоче дерево git — один `git add` і закритий ключ у репозиторії.
# Перевіряється ПЕРШОЮ (до пароля й до модуля PKI): шлях повний, вгору шукається `.git` (тека або файл worktree).
if ($BackupPfxPath) {
    $pfxFull = [System.IO.Path]::GetFullPath($BackupPfxPath)
    for ($dir = [System.IO.Path]::GetDirectoryName($pfxFull); $dir; $dir = [System.IO.Path]::GetDirectoryName($dir)) {
        if (Test-Path -LiteralPath (Join-Path $dir '.git')) {
            throw "-BackupPfxPath $pfxFull лежить у git-репозиторії ($dir): закритий ключ не зберігається в репозиторії — оберіть теку поза ним (офлайн-носій)."
        }
    }
}

if ($BackupPfxPath -and -not $PfxPassword) {
    throw '-BackupPfxPath потребує -PfxPassword (SecureString): резервна копія ключа без пароля не створюється.'
}
if (Test-Path -LiteralPath $CerPath) { throw "файл $CerPath уже існує — не перезаписую" }
if ($BackupPfxPath -and (Test-Path -LiteralPath $BackupPfxPath)) { throw "файл $BackupPfxPath уже існує — не перезаписую" }

$exportPolicy = if ($BackupPfxPath) { 'Exportable' } else { 'NonExportable' }
$location = "Cert:\$Store\My"

if (-not $PSCmdlet.ShouldProcess("$location ($Subject, $Years р., ключ $exportPolicy)", 'New-SelfSignedCertificate -Type CodeSigningCert')) {
    Write-Host "План: $location, $Subject, строк $Years р., ключ $exportPolicy; .cer -> $CerPath$(if ($BackupPfxPath) { "; PFX -> $BackupPfxPath" })"
    return
}

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject `
    -CertStoreLocation $location -KeyExportPolicy $exportPolicy `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears($Years)

Export-Certificate -Cert $cert -FilePath $CerPath -Type CERT | Out-Null
if ($BackupPfxPath) {
    Export-PfxCertificate -Cert $cert -FilePath $BackupPfxPath -Password $PfxPassword | Out-Null
}

Write-Host "Сертифікат підпису: $($cert.Subject), дійсний до $($cert.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Відбиток: $($cert.Thumbprint)"
Write-Host "Відкрита частина: $CerPath$(if ($BackupPfxPath) { " | резервна копія з ключем: $BackupPfxPath (зберігати офлайн)" })"
Write-Host ''
Write-Host 'Далі (docs/build/MSI-SIGNING-OPTIONS.md, «Видача: A+B»):'
Write-Host "  один раз тут, щоб verify-msi бачив підпис довіреним: Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\CurrentUser\Root"
Write-Host "  .\tools\sign-msi.ps1 -MsiPath <Ecr.msi> -Thumbprint $($cert.Thumbprint)"
Write-Host "  .\tools\verify-msi.ps1 -MsiPath <Ecr.msi> -IntegrityOnly -TrustedThumbprint $($cert.Thumbprint)"
Write-Host "  на машинах замовника: GPO (Trusted Publishers + Trusted Root) або"
Write-Host "  certutil -addstore TrustedPublisher $(Split-Path $CerPath -Leaf); certutil -addstore Root $(Split-Path $CerPath -Leaf)"
