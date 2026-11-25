<#
.SYNOPSIS
    Starts a live-window start script DETACHED (WMI Win32_Process Create) and proves the API writes its server log.

.DESCRIPTION
    b14 source-test-flert, AC-4. On live (b12, b13) the server log looked empty: the newest file in
    %ProgramData%\ECR\logs showed a LastWriteTime/size from the seed run, not from the API. The API did write - it
    appended to that same file (ecr-<date>_NNN.log), and Windows does not refresh the size and the last-write time
    in a directory listing while a file is held open. Get-ChildItem therefore cannot prove (or disprove) logging.

    This script reads the log files themselves (FileShare.ReadWrite) and looks for the start-up marker the API
    writes for ITS OWN process: "... (pid=<PID>)." (FileLog.ReportFileLog). No marker within -LogLineTimeoutSec
    (30 s by default) after the API answered /health/live = FAIL, exit 1.

    Two modes:
      * launch:  -StartScript <path> -ExitFile <path> -ApiPort <port> [-StartArguments '...']
                 launches the start script through WMI (parent WmiPrvSE, so live survives a desktop restart), waits
                 for the start script to write OK/FAIL into -ExitFile, takes the PID listening on -ApiPort, checks
                 the log.
      * check:   -ProcessId <pid>   only checks the log of an already running API (no launch, no port lookup).

    The start script itself is unchanged: it sets the environment and starts the API (see the window toolkit).

.EXAMPLE
    powershell -File tools\live\Start-LiveDetached.ps1 -StartScript E:\...\start-live-b14.ps1 `
        -StartArguments '-ExpectHash abc1234' -ExitFile E:\...\logs\start-live.exit -ApiPort 5094

.EXAMPLE
    powershell -File tools\live\Start-LiveDetached.ps1 -ProcessId 32856
#>
[CmdletBinding()]
param(
    [string] $StartScript,
    [string] $StartArguments = '',
    [string] $ExitFile,
    [int] $ApiPort = 0,
    [int] $ProcessId = 0,
    [string] $LogDirectory = '%ProgramData%\ECR\logs',
    [int] $TimeoutSec = 900,
    [int] $LogLineTimeoutSec = 30,
    [int] $TailBytes = 8MB
)

$ErrorActionPreference = 'Stop'

function Resolve-EcrLogDirectory([string] $Configured) {
    # The same expansion as FileLog.ResolveDirectory in the product.
    $expanded = [Environment]::ExpandEnvironmentVariables($Configured.Trim())
    if ($expanded.Contains('%')) { throw "log directory '$Configured' did not expand: $expanded" }
    return [IO.Path]::GetFullPath($expanded)
}

function Read-FileTail([string] $Path, [long] $Bytes) {
    # Open with ReadWrite+Delete sharing: the API keeps the file open (shared: false in Serilog means it is the only
    # WRITER; readers are allowed). Length here is the REAL length, unlike the directory listing.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $start = [Math]::Max([long]0, $stream.Length - $Bytes)
        [void] $stream.Seek($start, [IO.SeekOrigin]::Begin)
        $reader = New-Object IO.StreamReader($stream, (New-Object Text.UTF8Encoding $false))
        return $reader.ReadToEnd()
    }
    finally { $stream.Dispose() }
}

function Find-EcrLogMarker([string] $Directory, [int] $Pid_, [long] $Bytes) {
    $marker = "(pid=$Pid_)."
    $days = @((Get-Date).ToString('yyyyMMdd'), (Get-Date).AddDays(-1).ToString('yyyyMMdd'))
    foreach ($file in [IO.Directory]::GetFiles($Directory, 'ecr-*.log')) {
        $name = [IO.Path]::GetFileName($file)
        if (-not ($days | Where-Object { $name.StartsWith("ecr-$_") })) { continue }
        try { $text = Read-FileTail $file $Bytes } catch [IO.IOException] { continue }
        foreach ($line in $text -split "`n") {
            if ($line.Contains($marker)) { return [pscustomobject]@{ File = $file; Line = $line.TrimEnd() } }
        }
    }
    return $null
}

function Wait-EcrLogMarker([string] $Directory, [int] $Pid_, [int] $Seconds, [long] $Bytes) {
    if (-not [IO.Directory]::Exists($Directory)) { throw "FAIL: log directory $Directory does not exist" }
    $deadline = (Get-Date).AddSeconds($Seconds)
    do {
        $hit = Find-EcrLogMarker $Directory $Pid_ $Bytes
        if ($hit) { return $hit }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    return $null
}

$directory = Resolve-EcrLogDirectory $LogDirectory

if ($ProcessId -le 0) {
    if (-not $StartScript -or -not $ExitFile -or $ApiPort -le 0) {
        throw 'launch mode needs -StartScript, -ExitFile and -ApiPort (or pass -ProcessId for check mode)'
    }
    if (-not (Test-Path -LiteralPath $StartScript)) { throw "start script not found: $StartScript" }

    Remove-Item -LiteralPath $ExitFile -ErrorAction SilentlyContinue
    $command = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$StartScript`" $StartArguments"
    $created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $command }
    if ($created.ReturnValue -ne 0) { throw "FAIL: Win32_Process.Create returned $($created.ReturnValue)" }
    Write-Host "launcher pid $($created.ProcessId)"

    $started = Get-Date
    while (-not (Test-Path -LiteralPath $ExitFile) -and ((Get-Date) - $started).TotalSeconds -lt $TimeoutSec) {
        Start-Sleep -Seconds 2
    }
    $exit = if (Test-Path -LiteralPath $ExitFile) { (Get-Content -LiteralPath $ExitFile -TotalCount 1).Trim() } else { 'TIMEOUT' }
    if ($exit -ne 'OK') {
        Write-Host "FAIL: start script reported '$exit' after $([int]((Get-Date) - $started).TotalSeconds) s"
        exit 1
    }

    $listener = Get-NetTCPConnection -State Listen -LocalPort $ApiPort -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) { Write-Host "FAIL: nothing listens on port $ApiPort after OK"; exit 1 }
    $ProcessId = [int] $listener.OwningProcess
}

$found = Wait-EcrLogMarker $directory $ProcessId $LogLineTimeoutSec $TailBytes
if (-not $found) {
    Write-Host "FAIL: no server log line '(pid=$ProcessId).' in $directory\ecr-<date>*.log within $LogLineTimeoutSec s"
    exit 1
}

Write-Host "OK: API pid $ProcessId writes its server log: $($found.File)"
Write-Host $found.Line
exit 0
