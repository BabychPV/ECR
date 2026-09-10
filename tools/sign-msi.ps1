<#
.SYNOPSIS
    Підписує зібраний MSI. Окремий крок — навмисно не входить у
    build-msi.ps1, бо збірка має проходити на машині без сертифіката.
.NOTES
    "Organization Name" нижче — заповнювач: реальне ім'я підписанта і
    сертифікат — факт конкретного розгортання, не рішення цього скрипта.
    Запускати лише там, де сертифікат встановлено.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [Parameter(Mandatory)] [string] $SubjectName
)
$ErrorActionPreference = 'Stop'

signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
    /n $SubjectName $MsiPath
if ($LASTEXITCODE) { throw "signtool завершився з кодом $LASTEXITCODE" }
