<#
.SYNOPSIS
    М'ютекс на файлі навколо `dotnet test`, доки паралельні лінії Хвилі 3
    діляться ОДНИМ реальним SQL Server (Testcontainers тут не працюють).

.DESCRIPTION
    Директива паралельного аудиту (2026-09-11), §6: кілька ліній, що
    одночасно виконують `dotnet test` проти того самого реального SQL
    Server, ділять стан бази — тести стають плаваючими, гейти червоніють
    без причини. Це другий за вагою (після нерозділеного журналу) генератор
    втраченого часу.

    Скрипт чекає файл-лок у %TEMP%, тримає його на час прогону
    `dotnet test`, знімає в `finally` (навіть при падінні тестів чи Ctrl+C
    через `try/finally`, не `trap`, — інакше лок лишився б навічно при
    перериванні). Доки одна лінія тримає лок, інші лінії НЕ простоюють:
    виконують build, frontend-гейти, першоособову перевірку (§7) — усе, що
    не б'є в ту саму базу.

.PARAMETER Arguments
    Аргументи, що передаються напряму в `dotnet test` (шлях до проєкту чи
    .sln, `--filter`, тощо). За замовчуванням — весь `ECR.sln`.

.EXAMPLE
    powershell -File tools/journal/testdb-lock.ps1
.EXAMPLE
    powershell -File tools/journal/testdb-lock.ps1 -Arguments 'tests/Ecr.Architecture.Tests'
#>
[CmdletBinding()]
param(
    [string[]] $Arguments = @('ECR.sln')
)

$ErrorActionPreference = 'Stop'

$lock = Join-Path $env:TEMP 'ecr-testdb.lock'

Write-Host "Чекаю м'ютекс тестової бази ($lock)..." -ForegroundColor DarkGray
while ($true) {
    try {
        New-Item -Path $lock -ItemType File -ErrorAction Stop | Out-Null
        break
    }
    catch {
        Start-Sleep -Seconds 5
    }
}

Write-Host "Лок узято — запускаю dotnet test $($Arguments -join ' ')" -ForegroundColor DarkGray
try {
    & dotnet test @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test завершився з кодом $LASTEXITCODE"
    }
}
finally {
    Remove-Item -Path $lock -Force -ErrorAction SilentlyContinue
    Write-Host 'Лок звільнено.' -ForegroundColor DarkGray
}
