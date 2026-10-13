<#
.SYNOPSIS
    Заводить чернетку нової версії методології як клон останньої ОПУБЛІКОВАНОЇ (етапи Build-LandTemplate.ps1
    LandParity / SubstanceApplicability / CompositionMass працюють лише по чернетках).

.DESCRIPTION
    Для кожного коду методології: якщо вже є версія не в стані Published - нічого не робить (повтор = 0 змін,
    друкує id чернетки); інакше POST /api/v1/methodologies/{id}/versions { versionNumber, copyFromVersionId =
    остання опублікована, level }. Правило категорії, склад речовин, формули, режим Strict і публікацію інструмент
    НЕ чіпає: це етапи Build (-Stage LandParity,CompositionMass,SubstanceApplicability -OnlyMethodology <код>) і
    окреме рішення власника методології (publish - роль-рецензент).

    За замовчуванням (і з -DryRun) лише читає: входить і друкує, що було б створено. Запис - тільки з -Execute,
    явним -SqlConnection (локальний сервер, база EcrLand*) і -ConfirmStand `<адреса>/<база>`. Лише локальний API.

.PARAMETER Methodology     Коди методологій (ECW_C12_03); «а,б» або список.
.PARAMETER VersionNumber   Номер нової версії (наприклад V2): задається явно, не вгадується.
.PARAMETER Level           Рівень версії (за замовчуванням Configuration).
.PARAMETER BaseUrl         Адреса API стенда (обов'язково, без дефолту).
.PARAMETER User            Обліковий запис (або $env:ECR_USER).
.PARAMETER Password        Пароль SecureString (або $env:ECR_PASSWORD).
.PARAMETER ConfirmStand    Підтвердження стенда (обов'язково, без дефолту): адреса API, host:port або host; для запису -
                           `<адреса>/<база>` (наприклад localhost:5094/EcrLandUi).
.PARAMETER SqlConnection   Явний рядок підключення стенда (для назви бази в підтвердженні; дефолту немає).
.PARAMETER Execute         Створити чернетки (без нього - лише план).
.PARAMETER DryRun          Явно лише план (перемагає -Execute).

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\New-LandMethodologyDraft.ps1 -Methodology ECW_C12_03 -VersionNumber V2 -BaseUrl http://localhost:5094 -ConfirmStand localhost:5094 -User land-owner -Password $pw
    .\New-LandMethodologyDraft.ps1 -Methodology ECW_C12_03 -VersionNumber V2 -BaseUrl http://localhost:5094 -User land-owner -Password $pw `
        -SqlConnection 'Server=localhost;Database=EcrLandUi;Integrated Security=true' -ConfirmStand localhost:5094/EcrLandUi -Execute
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Methodology,
    [Parameter(Mandatory)][string]$VersionNumber,
    [string]$Level = 'Configuration',
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [string]$User,
    [securestring]$Password,
    [string]$SqlConnection,
    [switch]$Execute,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandInputCells.Lib.ps1')
. (Join-Path $here 'LandStandSession.Lib.ps1')
Set-StrictMode -Off

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
# Охорона стенда - ДО входу й будь-якого виклику.
$null = Initialize-LandStandSession -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand -SqlConnection $SqlConnection -ForWrite:$exec

$codes = @(ConvertTo-LandStringList $Methodology)
if ($codes.Count -eq 0) { throw 'Задайте -Methodology: коди методологій.' }
foreach ($c in $codes) { if ($c -notmatch '^ECW_[A-Za-z0-9_]+$') { throw "Код методології '$c' не схожий на ECW_*: відмова." } }

function Get-ItemsOf($Json) {
    if ($null -eq $Json) { return @() }
    if ($Json -is [array]) { return @($Json) }
    if ($null -ne $Json.PSObject.Properties['items']) { return @($Json.items) }
    return @($Json)
}

Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password
$all = Invoke-Ecr -Method GET -Path '/api/v1/methodologies?limit=500'
Assert-EcrOk $all 'GET methodologies'
$byCode = @{}
foreach ($m in (Get-ItemsOf $all.Json)) { $byCode[[string]$m.code] = [int]$m.id }

$failed = 0
foreach ($c in $codes) {
    if (-not $byCode.ContainsKey($c)) { Write-Host "  ! ${c}: методології на стенді немає"; $failed++; continue }
    $mid = $byCode[$c]
    $vr = Invoke-Ecr -Method GET -Path "/api/v1/methodologies/$mid/versions"
    Assert-EcrOk $vr "GET versions ($c)"
    $versions = @(Get-ItemsOf $vr.Json)
    $draft = $versions | Where-Object { [string]$_.status -ne 'Published' } | Sort-Object { [int]$_.id } -Descending | Select-Object -First 1
    if ($draft) { Write-Host "  ${c}: чернетка вже є (версія $($draft.id), $($draft.versionNumber)) - 0 змін"; continue }
    $source = $versions | Where-Object { [string]$_.status -eq 'Published' } | Sort-Object { [int]$_.id } -Descending | Select-Object -First 1
    if (-not $source) { Write-Host "  ! ${c}: немає опублікованої версії для клону"; $failed++; continue }
    if (-not $exec) { Write-Host "  [план] ${c}: створив би $VersionNumber з копії версії $($source.id) (рівень $Level)"; continue }
    $post = Invoke-Ecr -Method POST -Path "/api/v1/methodologies/$mid/versions" -Body @{ versionNumber = $VersionNumber; copyFromVersionId = [int]$source.id; level = $Level }
    if ($post.Status -eq 200 -or $post.Status -eq 201) { Write-Host "  ${c}: створено чернетку $VersionNumber (версія $($post.Json.id)) з копії версії $($source.id)" }
    else { Write-Host "  ! ${c}: HTTP $($post.Status) $(Get-EcrErrorCode $post)"; $failed++ }
}
if (-not $exec) { Write-Host 'ПЛАН (лише читання). Запис: -Execute -SqlConnection <локальний, EcrLand*> -ConfirmStand <адреса>/<база>.' }
if ($failed -gt 0) { exit 1 }
