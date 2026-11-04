<#
.SYNOPSIS
    Scratch-стенд HSE401: (пере)створення бази EcrHse401Scratch малими файловими групами (-SmallFiles, -DataPath F:\EcrData),
    запуск/зупинка API на порту 5180. PID запущених процесів пишуться в artifacts\hse401-api.pids; зупинка - лише за ними.

.PARAMETER Action
    Create (DROP+CREATE бази через tools/setup-dev-db.ps1 -SmallFiles), Start, Stop, Status.
.NOTES
    Не торкається EcrLandUi*, EcrDev, ECR, EcrF13p157, EcrContractL2 та портів 5094/6094. Перед важким кроком перевіряє
    H:\ECR\.sync-local\WINDOW-ACTIVE (є файл - чекає, перевірка раз на 5 хв, до 60 хв).
#>
[CmdletBinding()]
param([ValidateSet('Create', 'Start', 'Stop', 'Status')][string]$Action = 'Status', [string]$DataPath = 'F:\EcrData', [string]$BootstrapPassword = 'Hse401-Scratch-2026!')
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent (Split-Path -Parent $here)
$db = 'EcrHse401Scratch'; $port = 5180
$art = Join-Path $root 'artifacts'
$pidFile = Join-Path $art 'hse401-api.pids'
if (-not (Test-Path $art)) { New-Item -ItemType Directory -Force $art | Out-Null }

function Wait-NoWindow {
    $flag = 'H:\ECR\.sync-local\WINDOW-ACTIVE'
    $deadline = (Get-Date).AddMinutes(60)
    while (Test-Path $flag) {
        if ((Get-Date) -gt $deadline) { throw 'WINDOW-ACTIVE не знято за 60 хв: зупинка.' }
        Write-Host 'WINDOW-ACTIVE є - чекаю 5 хв'; Start-Sleep -Seconds 300
    }
}
function Stop-Mine {
    if (-not (Test-Path $pidFile)) { Write-Host 'pids-файлу немає: нічого зупиняти'; return }
    foreach ($id in (Get-Content $pidFile) -split ',') {
        $id = [int]($id.Trim())
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -in 'dotnet', 'Ecr.Api') { $ErrorActionPreference = 'Continue'; cmd /c "taskkill /T /F /PID $id >nul 2>&1"; $ErrorActionPreference = 'Stop'; Write-Host "зупинено PID $id ($($p.ProcessName))" }
    }
    Remove-Item $pidFile -Force
}
function Start-Api {
    if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Порт $port зайнятий." }
    $env:ECR_ConnectionStrings__Ecr = "Server=localhost;Database=$db;Trusted_Connection=True;TrustServerCertificate=True"
    $env:ECR_Bootstrap__Password = $BootstrapPassword
    $env:ASPNETCORE_URLS = "http://localhost:$port"
    $env:ECR_Auth__DataProtection__AllowUnprotectedKeys = 'true'
    $log = Join-Path $art 'hse401-api.log'
    $p = Start-Process -PassThru -FilePath dotnet -ArgumentList ('run --project "{0}" --no-build --no-launch-profile' -f (Join-Path $root 'src\Ecr.Api')) -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden
    $ready = $false
    foreach ($i in 1..90) {
        Start-Sleep -Seconds 2
        try { if ((Invoke-WebRequest "http://localhost:$port/health/live" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200) { $ready = $true; break } } catch { }
    }
    if (-not $ready) { throw "API не піднявся за 180 с (див. $log.err)" }
    $child = Get-CimInstance Win32_Process -Filter "Name='Ecr.Api.exe'" | Where-Object { $_.ParentProcessId -eq $p.Id } | Select-Object -First 1
    $ids = @($p.Id); if ($child) { $ids += $child.ProcessId }
    ($ids -join ',') | Out-File $pidFile -Encoding ascii
    Write-Host "API на http://localhost:$port, PID $($ids -join ',')"
}

switch ($Action) {
    'Status' {
        $l = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        Write-Host ("порт {0}: {1}; pids: {2}" -f $port, $(if ($l) { "слухає PID $($l.OwningProcess)" } else { 'вільний' }), $(if (Test-Path $pidFile) { Get-Content $pidFile } else { '-' }))
        sqlcmd -S localhost -E -Q "SET NOCOUNT ON; SELECT CAST(SUM(size)*8.0/1024 AS decimal(10,1)) AS MB FROM sys.master_files WHERE database_id=DB_ID('$db')" -h -1
    }
    'Stop' { Stop-Mine }
    'Start' { Wait-NoWindow; Start-Api }
    'Create' {
        Wait-NoWindow
        Stop-Mine
        & powershell -NoProfile -File (Join-Path $root 'tools\setup-dev-db.ps1') -Server localhost -Database $db -Documents 0 -DataPath $DataPath -SmallFiles -RequireFreeGb 2 -BootstrapPassword $BootstrapPassword
        if ($LASTEXITCODE) { throw "setup-dev-db.ps1 -> $LASTEXITCODE" }
        $acc = Join-Path $art 'hse401-accounts.tsv'; if (Test-Path $acc) { Remove-Item $acc -Force }
        $o = Join-Path $here 'out'; foreach ($f in 'hse401-state.json', 'hse401-nae-map.json') { $x = Join-Path $o $f; if (Test-Path $x) { Remove-Item $x -Force } }
        Write-Host 'База створена; далі: Start-Hse401Stand.ps1 -Action Start'
    }
}
