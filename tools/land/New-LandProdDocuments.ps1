<#
.SYNOPSIS
    Creates the empty documents LAND-PROD-<n> (all sheets of the published LAND_UI version) in a LOCAL stand and prints
    the -DocMap JSON for Load-LandRows.ps1. Idempotent: an existing document with the name is reused.
.DESCRIPTION
    Same call as Stage-Documents of Build-LandTemplate.ps1 (POST /api/v1/documents). Writes through the API only.
.PARAMETER Count            how many documents (LAND-PROD-1..Count)
.PARAMETER BaseUrl          local API (mandatory)
.PARAMETER ConfirmStand     <address>/<database> (mandatory), as everywhere in tools/land
.PARAMETER SqlConnection    explicit local connection string to the EcrLand* stand (names the database)
.PARAMETER User / Password  ECR account (or ECR_USER / ECR_PASSWORD)
.PARAMETER ProjectCode      project of the stand (default LANDUI2026)
.PARAMETER TemplateCode     template (default LAND_UI)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$Count,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [Parameter(Mandatory)][string]$SqlConnection,
    [string]$User,
    [securestring]$Password,
    [string]$ProjectCode = 'LANDUI2026',
    [string]$TemplateCode = 'LAND_UI'
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandStandSession.Lib.ps1')
$null = Initialize-LandStandSession -BaseUrl $BaseUrl -ConfirmStand $ConfirmStand -SqlConnection $SqlConnection -ForWrite
Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password

function Get-Items($Json) { if ($null -ne $Json -and $Json.PSObject.Properties['items']) { return @($Json.items) } return @($Json) }

$project = Get-Items (Invoke-Ecr -Method GET -Path '/api/v1/projects').Json | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
if (-not $project) { throw "Project $ProjectCode not found." }
$tpl = Get-Items (Invoke-Ecr -Method GET -Path ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
$pub = Get-Items (Invoke-Ecr -Method GET -Path "/api/v1/templates/$($tpl.id)/versions?limit=100").Json | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
$structure = (Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$($pub.id)/structure").Json
$sheetIds = @($structure.sheets | ForEach-Object { [int]$_.id })

$existing = @{}
foreach ($d in (Get-Items (Invoke-Ecr -Method GET -Path "/api/v1/documents?projectId=$($project.id)&limit=200").Json)) {
    $n = $d.nameL10n
    $name = if ($null -ne $n -and $n.PSObject.Properties['values']) { [string]$n.values.en } elseif ($null -ne $n) { [string]$n.en } else { [string]$d.businessKey }
    $existing[$name] = [int]$d.id
}
$map = [ordered]@{}
for ($i = 1; $i -le $Count; $i++) {
    $name = "LAND-PROD-$i"
    if ($existing.ContainsKey($name)) { $map[$name] = $existing[$name]; Write-Host "$name exists (id $($existing[$name]))"; continue }
    $r = Invoke-Ecr -Method POST -Path '/api/v1/documents' -Body @{ name = @{ en = $name }; projectId = [int]$project.id; sheetDefIds = $sheetIds; templateVersionId = [int]$pub.id }
    if ($r.Status -ge 300) { throw "POST documents ${name}: HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
    $id = if ($r.Json.documentId) { [int]$r.Json.documentId } else { [int]$r.Json.id }
    $map[$name] = $id
    Write-Host "$name created (id $id)"
}
Write-Output ($map | ConvertTo-Json -Compress)
