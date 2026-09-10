<#
.SYNOPSIS
    Ребілд Ecr.Api, потім збірка MSI одним викликом.
.EXAMPLE
    .\tools\rebuild-and-package-msi.ps1 -Version 1.0.0
.NOTES
    Обгортка над tools/build-msi.ps1, яка сама публікує застосунок
    (`dotnet publish` теж компілює код) — тож ребілд тут не заміняє
    публікацію, а йде ПЕРЕД нею окремим кроком: помилка компіляції
    виявляється за секунди, а не після кількох хвилин self-contained
    публікації й WiX-пакування, які на зламаному коді все одно не
    дійшли б до кінця.

    Як і build-msi.ps1: не входить у `dotnet build Ecr.sln` (WiX не
    збирається під Linux, CI цього репозиторію — лише ubuntu-latest,
    docs/build/10-installer.md). Запускати на Windows-машині збірки.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ⛔ Q-217 (реальний прогін): PowerShell перетворює КОЖЕН запис нативної
# команди в stderr на запис у потоці помилок, і $ErrorActionPreference =
# 'Stop' зупиняє скрипт на цьому записі незалежно від коду виходу —
# "npm warn deprecated ..." зупинило build-msi.ps1 саме так. Не про
# $PSNativeCommandUseErrorActionPreference (за замовчуванням і так
# $false). Єдине надійне джерело істини — фактичний код виходу.
function Invoke-NativeStep {
    param(
        [Parameter(Mandatory)] [string] $Description,
        [Parameter(Mandatory)] [scriptblock] $Command
    )

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Command
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE) {
        throw "$Description завершився з кодом $LASTEXITCODE"
    }
}

$root       = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $root 'src\Ecr.Api\Ecr.Api.csproj'
$buildMsi   = Join-Path $root 'tools\build-msi.ps1'

Write-Host "== Ребілд Ecr.Api ($Configuration) ==" -ForegroundColor Cyan

# --no-incremental: повний перебуд, а не «те, що MSBuild вважає зміненим».
# Саме так спливають помилки, які інкрементна збірка могла тихо
# пропустити на застарілому кеші (обіч цієї сесії таке вже траплялося
# з WiX-проєктом — коротший, дешевший спосіб перевірити тут вартий того).
Invoke-NativeStep "dotnet build (Ecr.Api)" {
    dotnet build $apiProject -c $Configuration --no-incremental
}

Write-Host ""
Write-Host "== Пакування MSI (версія $Version) ==" -ForegroundColor Cyan

# Без -SkipPublish: build-msi.ps1 публікує застосунок наново з нуля.
# Ребілд вище — лише швидка перевірка «код компілюється», не заміна
# self-contained публікації, яку MSI пакує.
& $buildMsi -Version $Version -Configuration $Configuration -Runtime $Runtime
