<#
.SYNOPSIS
  Разовий імпорт складу газу FLERT → ECR як крок вікна (D-NEW, людина 24.11): спершу сухий прогін, потім імпорт і звірка.

.DESCRIPTION
  -DryRun (типово): лише ЧИТАЄ. FLERT — кількості потоків/кейсів/складів/ненульових компонентів, Σ і CHECKSUM_AGG
  компонентів (дублі CO/Carbon_Monoxide, H2/Hydrogen, O2/Oxygen згорнуто до короткої назви, як в імпорті); ECR — чи вже є
  потоки (є — імпорт неможливий, вихід 2).
  -VerifyOnly: лише звірка вже імпортованого ECR із FLERT (кількості, Σ, контрольна сума); вихід 3 при розбіжності.
  -Apply: те саме, далі POST /api/v1/gas-composition/import від імені оператора (права GasComposition.Edit і
  Integration.Manage), очікування прогону itg.MaintenanceRun «flert-composition-sync» і звірка ECR з FLERT тими самими
  кількостями й контрольними сумами. Розбіжність — вихід 3.

  ⛔ У FLERT скрипт не пише нічого (лише SELECT). Імпорт робить застосунок (джерело ext.DataSource «FLERT» і запит
  Secrets:Sql:FLERT:CompositionQuery мають бути налаштовані — runbook §4.3). Повторний запуск безпечний: у непорожню
  базу імпорт відмовляє.

.EXAMPLE
  powershell -File tools/gas-composition/Import-GasCompositionFromFlert.ps1 -EcrServer localhost -EcrDatabase EcrLandUi -FlertServer NCATSQLV42_PI
.EXAMPLE
  $cred = Get-Credential; powershell -File tools/gas-composition/Import-GasCompositionFromFlert.ps1 -EcrServer localhost -EcrDatabase EcrLandUi `
    -FlertServer NCATSQLV42_PI -Apply -BaseUrl https://ecr.local -Credential $cred
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $EcrServer,
    [Parameter(Mandatory)] [string] $EcrDatabase,
    [Parameter(Mandatory)] [string] $FlertServer,
    [string] $FlertDatabase = 'FLERT',
    [switch] $Apply,
    [switch] $VerifyOnly,
    [string] $BaseUrl,
    [System.Management.Automation.PSCredential] $Credential,
    [int] $TimeoutMinutes = 15
)

$ErrorActionPreference = 'Stop'

function Invoke-Sql([string] $Server, [string] $Database, [string] $Query) {
    $file = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($file, "SET NOCOUNT ON;`n" + $Query, (New-Object Text.UTF8Encoding $false))
        $out = & sqlcmd -S $Server -E -d $Database -b -I -W -h -1 -s '|' -f 65001 -i $file
        if ($LASTEXITCODE -ne 0) { throw "sqlcmd ($Server/$Database) повернув $LASTEXITCODE`n$($out -join "`n")" }
        # ⚠ Кома: інакше масив з одного рядка PowerShell розгортає в рядок, і [0] — перший СИМВОЛ.
        return ,@($out | Where-Object { $_ -and $_.Trim() })
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}

# FLERT: потоки, кейси, склади, ненульові компоненти (коротка назва перемагає довгу), Σ і контрольна сума.
$flertQuery = @'
WITH u AS (
  SELECT t.ID AS TypeId, v.Col, v.Val
    FROM dbo.FlaringStreamType t
    JOIN dbo.FlaringStreamGasComposition g ON g.StreamTypeID = t.ID
   CROSS APPLY (VALUES
     (N'Nitrogen', g.Nitrogen), (N'CO2', g.CO2), (N'H2S', g.H2S), (N'Methane', g.Methane), (N'Ethane', g.Ethane), (N'Propane', g.Propane),
     (N'IC4_1', g.IC4_1), (N'NC4_1', g.NC4_1), (N'IC5_1', g.IC5_1), (N'NC5_1', g.NC5_1), (N'C6_1', g.C6_1), (N'Benzene', g.Benzene),
     (N'C7_1', g.C7_1), (N'Toluene', g.Toluene), (N'C8_1', g.C8_1), (N'pXylene', g.pXylene), (N'EBenzene', g.EBenzene), (N'C9_1', g.C9_1),
     (N'C10_1', g.C10_1), (N'C11_1', g.C11_1), (N'C12_1', g.C12_1), (N'C13_1', g.C13_1), (N'C14_1', g.C14_1), (N'CN1_2', g.CN1_2),
     (N'CN2_2', g.CN2_2), (N'CN3_1', g.CN3_1), (N'MMercaptan', g.MMercaptan), (N'EMercaptan', g.EMercaptan), (N'nPMercaptan', g.nPMercaptan),
     (N'nBMercaptan', g.nBMercaptan), (N'CS2', g.CS2), (N'COS', g.COS), (N'H2O', g.H2O), (N'TEGlycol', g.TEGlycol),
     (N'Carbon_Monoxide', g.Carbon_Monoxide), (N'Hydrogen', g.Hydrogen), (N'Oxygen', g.Oxygen),
     (N'SO2', g.SO2), (N'O2', g.O2), (N'NH3', g.NH3), (N'H2', g.H2), (N'CO', g.CO), (N'C2H7NO', g.C2H7NO), (N'Sulfur', g.Sulfur),
     (N'C2H6S2', g.C2H6S2), (N'C3H8S2', g.C3H8S2), (N'C4H10S2', g.C4H10S2), (N'C4H11NO2', g.C4H11NO2), (N'Methanol', g.Methanol)) AS v(Col, Val)
), r AS (
  SELECT s.TypeId, s.Col AS Component, COALESCE(NULLIF(s.Val, 0), NULLIF(l.Val, 0)) AS MolPct
    FROM u s
    LEFT JOIN u l ON l.TypeId = s.TypeId
                 AND l.Col = CASE s.Col WHEN N'CO' THEN N'Carbon_Monoxide' WHEN N'H2' THEN N'Hydrogen' WHEN N'O2' THEN N'Oxygen' END
   WHERE s.Col NOT IN (N'Carbon_Monoxide', N'Hydrogen', N'Oxygen')
     AND COALESCE(NULLIF(s.Val, 0), NULLIF(l.Val, 0)) IS NOT NULL
)
SELECT (SELECT COUNT(*) FROM dbo.FlaringStream), (SELECT COUNT(*) FROM dbo.FlaringStreamType),
       (SELECT COUNT(*) FROM dbo.FlaringStreamGasComposition), (SELECT COUNT(*) FROM r),
       (SELECT CAST(SUM(CAST(MolPct AS decimal(38, 16))) AS nvarchar(60)) FROM r),
       (SELECT CHECKSUM_AGG(CHECKSUM(TypeId, Component, CAST(MolPct AS decimal(28, 16)))) FROM r);
'@

# ECR: ті самі показники з довідників STREAM / STREAM_CASE / GAS_COMPOSITION (живі, увімкнені рядки).
$ecrQuery = @'
DECLARE @stream int = (SELECT Id FROM cfg.RegistryDef WHERE Code = N'STREAM');
DECLARE @case int = (SELECT Id FROM cfg.RegistryDef WHERE Code = N'STREAM_CASE');
DECLARE @comp int = (SELECT Id FROM cfg.RegistryDef WHERE Code = N'GAS_COMPOSITION');
WITH c AS (
  SELECT e.Id, MAX(CASE WHEN f.Code = N'LEGACY_TYPE_ID' THEN CAST(v.ValueNumeric AS int) END) AS TypeId,
               MAX(CASE WHEN f.Code = N'LEGACY_COMPOSITION_ID' THEN CAST(v.ValueNumeric AS int) END) AS CompId
    FROM dic.RegistryEntry e
    JOIN dic.RegistryValue v ON v.RegistryEntryId = e.Id
    JOIN cfg.RegistryFieldDef f ON f.Id = v.RegistryFieldDefId
   WHERE e.RegistryDefId = @case AND e.IsDeleted = 0
   GROUP BY e.Id
), r AS (
  SELECT c.TypeId, ce.Code AS Component, mv.ValueNumeric AS MolPct
    FROM dic.RegistryEntry re
    JOIN dic.RegistryValue cv ON cv.RegistryEntryId = re.Id
    JOIN cfg.RegistryFieldDef cf ON cf.Id = cv.RegistryFieldDefId AND cf.Code = N'CASE'
    JOIN c ON c.Id = cv.ValueRefEntryId
    JOIN dic.RegistryValue pv ON pv.RegistryEntryId = re.Id
    JOIN cfg.RegistryFieldDef pf ON pf.Id = pv.RegistryFieldDefId AND pf.Code = N'COMPONENT'
    JOIN dic.RegistryEntry ce ON ce.Id = pv.ValueRefEntryId
    JOIN dic.RegistryValue mv ON mv.RegistryEntryId = re.Id
    JOIN cfg.RegistryFieldDef mf ON mf.Id = mv.RegistryFieldDefId AND mf.Code = N'MOL_PCT'
   WHERE re.RegistryDefId = @comp AND re.IsDeleted = 0 AND re.IsActive = 1
)
SELECT (SELECT COUNT(*) FROM dic.RegistryEntry WHERE RegistryDefId = @stream AND IsDeleted = 0),
       (SELECT COUNT(*) FROM c), (SELECT COUNT(DISTINCT CompId) FROM c), (SELECT COUNT(*) FROM r),
       (SELECT CAST(SUM(CAST(MolPct AS decimal(38, 16))) AS nvarchar(60)) FROM r),
       (SELECT CHECKSUM_AGG(CHECKSUM(TypeId, Component, CAST(MolPct AS decimal(28, 16)))) FROM r);
'@

$names = 'потоки', 'кейси (версії)', 'склади', 'ненульові компоненти', 'Σ мол.% усіх компонентів', 'контрольна сума'

function Read-Metrics([string] $Server, [string] $Database, [string] $Query) {
    return ((Invoke-Sql $Server $Database $Query)[0] -split '\|') | ForEach-Object { $_.Trim() }
}

function Get-EcrStreams {
    $rows = Invoke-Sql $EcrServer $EcrDatabase @'
SELECT COUNT(*) FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId WHERE d.Code = N'STREAM' AND e.IsDeleted = 0;
'@
    return [int]$rows[0]
}

Write-Host "FLERT ($FlertServer/$FlertDatabase) — лише читання:"
$flert = Read-Metrics $FlertServer $FlertDatabase $flertQuery
for ($i = 0; $i -lt $names.Count; $i++) { Write-Host ("  {0,-28} {1}" -f $names[$i], $flert[$i]) }

function Compare-Metrics {
    $ecr = Read-Metrics $EcrServer $EcrDatabase $ecrQuery
    $bad = 0
    Write-Host ("  {0,-28} {1,-32} {2}" -f 'показник', 'FLERT', 'ECR')
    for ($i = 0; $i -lt $names.Count; $i++) {
        $same = $flert[$i] -eq $ecr[$i]
        if (-not $same) { $bad++ }
        Write-Host ("  {0,-28} {1,-32} {2} {3}" -f $names[$i], $flert[$i], $ecr[$i], $(if ($same) { 'OK' } else { 'РОЗБІЖНІСТЬ' }))
    }

    if ($bad -gt 0) { Write-Host "Розбіжностей: $bad — імпорт не прийнято."; exit 3 }
    Write-Host 'ECR збігається з FLERT за кількостями, Σ і контрольною сумою.'
    exit 0
}

if ($VerifyOnly) { Compare-Metrics }

$existing = Get-EcrStreams
Write-Host "ECR ($EcrServer/$EcrDatabase): потоків складу газу — $existing"
if ($existing -gt 0) {
    Write-Host 'ECR уже має склад газу: імпорт у непорожню базу заборонено (правки ECR не перетираються). Нічого не робимо.'
    exit 2
}

if (-not $Apply) {
    Write-Host 'Сухий прогін: нічого не записано. Для імпорту — той самий виклик із -Apply -BaseUrl … -Credential ….'
    exit 0
}

if (-not $BaseUrl -or -not $Credential) { throw '-Apply вимагає -BaseUrl і -Credential (оператор із правами GasComposition.Edit та Integration.Manage).' }

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$login = @{ userName = $Credential.UserName; password = $Credential.GetNetworkCredential().Password } | ConvertTo-Json -Compress
Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/api/v1/login/local" -Method POST -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($login)) -WebSession $session -TimeoutSec 120 | Out-Null

$lastRun = [long]((Invoke-Sql $EcrServer $EcrDatabase "SELECT ISNULL(MAX(Id), 0) FROM itg.MaintenanceRun WHERE JobCode = N'flert-composition-sync';")[0])
$accepted = Invoke-WebRequest -UseBasicParsing -Uri "$BaseUrl/api/v1/gas-composition/import" -Method POST -WebSession $session -TimeoutSec 120
Write-Host "Імпорт поставлено: $($accepted.Content)"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 10
    $run = Invoke-Sql $EcrServer $EcrDatabase "SELECT TOP 1 Status, ISNULL(LEFT(DetailsJson, 400), N'') FROM itg.MaintenanceRun WHERE JobCode = N'flert-composition-sync' AND Id > $lastRun AND FinishedAt IS NOT NULL ORDER BY Id;"
} while ($run.Count -eq 0 -and (Get-Date) -lt $deadline)

if ($run.Count -eq 0) { throw "Імпорт не завершився за $TimeoutMinutes хв — перевірте Jobs у застосунку." }
Write-Host "Прогін: $($run[0])"

Compare-Metrics
