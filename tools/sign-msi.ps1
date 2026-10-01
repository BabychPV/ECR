<#
.SYNOPSIS
    Підписує зібраний MSI. Окремий крок — навмисно не входить у
    build-msi.ps1, бо збірка має проходити на машині без сертифіката.
.DESCRIPTION
    Два шляхи (MSI-SIGNING-OPTIONS (A+B), QUESTIONS-BUSINESS §3.3):
      -Thumbprint   — варіант A: власний (самопідписаний) code-signing
                      сертифікат зі сховища Cert:\CurrentUser\My або
                      Cert:\LocalMachine\My; Set-AuthenticodeSignature, без signtool.
      -SubjectName  — варіант C: сертифікат корпоративного ЦС через signtool
                      (ключ може бути на токені/HSM).
    Після підпису файл змінюється, тож скрипт ПЕРЕПИСУЄ `<MsiPath>.sha256`
    (який лишив build-msi.ps1): у release notes іде хеш уже підписаного файла.
.PARAMETER TimestampServer
    Сервер позначки часу (RFC 3161). Без неї підпис стає недійсним, щойно
    сплине термін сертифіката. Типово для -SubjectName — DigiCert (як і
    раніше); для -Thumbprint — без позначки (машина видачі може бути без
    інтернету), задайте явно, якщо доступ є.
.NOTES
    Конкретний сертифікат і ім'я підписанта — факт розгортання, не рішення
    скрипта. Запускати лише там, де сертифікат встановлено.
    Перевірка результату — tools/verify-msi.ps1 -IntegrityOnly -TrustedThumbprint <відбиток>.
#>
[CmdletBinding(DefaultParameterSetName = 'Subject')]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [Parameter(Mandatory, ParameterSetName = 'Subject')] [string] $SubjectName,
    [Parameter(Mandatory, ParameterSetName = 'Thumbprint')] [string] $Thumbprint,
    [string] $TimestampServer
)
$ErrorActionPreference = 'Stop'
$MsiPath = (Resolve-Path -LiteralPath $MsiPath).Path

if ($PSCmdlet.ParameterSetName -eq 'Thumbprint') {
    $Thumbprint = $Thumbprint -replace '\s', ''
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq $Thumbprint } | Select-Object -First 1
    if (-not $cert) { throw "сертифіката з відбитком $Thumbprint немає в CurrentUser\My і LocalMachine\My" }
    if (-not $cert.HasPrivateKey) { throw "сертифікат $Thumbprint без закритого ключа — підписати ним не можна" }
    $signArgs = @{ LiteralPath = $MsiPath; Certificate = $cert; HashAlgorithm = 'SHA256' }
    if ($TimestampServer) { $signArgs.TimestampServer = $TimestampServer }
    $sig = Set-AuthenticodeSignature @signArgs
    # Status тут — стан ПІСЛЯ підпису: для самопідписаного без довіри на цій
    # машині це UnknownError/NotTrusted, і підпис усе одно записано. Провал —
    # лише коли підпису у файлі немає.
    if (-not $sig.SignerCertificate -or $sig.Status -in 'NotSigned', 'HashMismatch') {
        throw "Set-AuthenticodeSignature: $($sig.Status) — $($sig.StatusMessage)"
    }
}
else {
    if (-not $TimestampServer) { $TimestampServer = 'http://timestamp.digicert.com' }
    # ⛔ Q-217: PowerShell перетворює запис нативної команди в stderr на
    # помилку, і $ErrorActionPreference = 'Stop' зупиняє скрипт на ньому
    # незалежно від коду виходу (signtool іноді пише в stderr нешкідливо,
    # напр. про CRL-перевірку). Тимчасове послаблення + явний $LASTEXITCODE —
    # єдине надійне джерело істини.
    $previousEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        signtool sign /fd SHA256 /tr $TimestampServer /td SHA256 `
            /n $SubjectName $MsiPath
    }
    finally {
        $ErrorActionPreference = $previousEap
    }
    if ($LASTEXITCODE) { throw "signtool завершився з кодом $LASTEXITCODE" }
}

# Формат sha256sum: `<hex>  <ім'я>` — читає verify-msi.ps1 і `sha256sum -c`.
$hash = (Get-FileHash -LiteralPath $MsiPath -Algorithm SHA256).Hash.ToLower()
Set-Content -LiteralPath "$MsiPath.sha256" -Value "$hash  $(Split-Path $MsiPath -Leaf)" -Encoding ascii
Write-Host "Підписано: $MsiPath"
Write-Host "SHA-256 (після підпису, у release notes): $hash"
