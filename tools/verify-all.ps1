<#
.SYNOPSIS
    Повна перевірка проєкту одним викликом: складання, тести, розгортання,
    клієнт.

.DESCRIPTION
    Скрипт існує через `P-17`. Його висновок був не про один зламаний файл, а
    про те, що **тести не перевіряють шлях розгортання**: вони виконують ті
    самі `.sql` іншим клієнтом, ніж DBA, і тому були зеленими, поки реальне
    розгортання падало. Те саме стосується клієнта: `dotnet test` нічого не
    знає ні про `tsc`, ні про `vitest`.

    Тут зібрано всі чотири перевірки в одному місці, щоб конвеєр складання
    викликав ОДНУ команду і не міг випадково пропустити одну з них. Який саме
    конвеєр — GitHub Actions, Azure DevOps чи локальний хук — скрипт не знає
    навмисно: платформу обирає замовник, а перелік перевірок від неї не
    залежить.

    ⚠ Порядок значущий і йде від дешевого до дорогого: складання падає за
    секунди, розгортання під `sqlcmd` — за хвилину. Зворотний порядок змусив
    би чекати найдовшу перевірку заради помилки, яку видно одразу.

.PARAMETER SkipDeployment
    Пропустити перевірку розгортання (потрібен `sqlcmd` і SQL Server).

.PARAMETER SkipClient
    Пропустити перевірку клієнта (потрібні Node.js і встановлені пакети).

.PARAMETER TestSql
    Рядок з'єднання для інтеграційних тестів. За замовчуванням береться
    `ECR_TEST_SQL` з оточення; без нього тести шукатимуть Docker.

.EXAMPLE
    powershell -File tools/verify-all.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipDeployment,
    [switch] $SkipClient,
    [string] $TestSql = $env:ECR_TEST_SQL
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$client = Join-Path $root 'src/Ecr.Web'
$failures = @()

function Step {
    param([string] $Name, [scriptblock] $Body)

    Write-Host ''
    Write-Host "── $Name" -ForegroundColor Cyan

    try {
        & $Body
        if ($LASTEXITCODE -ne 0) {
            throw "код виходу $LASTEXITCODE"
        }
    }
    catch {
        # ⚠ Перевірки НЕ зупиняють одна одну: інакше зламаний клієнт ховав би
        # стан розгортання, і кожен прогін показував би рівно одну проблему.
        $script:failures += "$Name — $($_.Exception.Message)"
        Write-Host "   ✗ $Name" -ForegroundColor Red
        return
    }

    Write-Host "   ✓ $Name" -ForegroundColor Green
}

Step 'Складання' {
    & dotnet build (Join-Path $root 'Ecr.sln') -v q --nologo
}

Step 'Тести .NET' {
    if ($TestSql) {
        # ⚠ Через оточення, а не аргументом: фікстура читає саме `ECR_TEST_SQL`
        # (`P-04`), і передати рядок з'єднання інакше нема куди.
        $env:ECR_TEST_SQL = $TestSql
    }

    & dotnet test (Join-Path $root 'Ecr.sln') --no-build -v q --nologo
}

if (-not $SkipDeployment) {
    Step 'Розгортання під sqlcmd' {
        & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-sql-scripts.ps1')
    }
}

if (-not $SkipDeployment) {
    # ⛔ Останнім кроком і навмисно: це єдина перевірка, яка запускає ЖИВИЙ
    # процес і проходить шлях користувача цілком. Саме він ламався в семи
    # місцях і не падав у жодному (`A7-25`…`A7-30`) при 616 зелених тестах.
    Step 'Наскрізний сценарій' {
        & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'smoke.ps1')
    }
}

if (-not $SkipClient) {
    Push-Location $client
    try {
        # ⚠ Саме `npm run`, а не `npx`: перевірки мусять запускати ТІ САМІ
        # команди, що й розробник (`package.json` §scripts). Крім того, `npx`
        # із PowerShell не знаходить локально встановлених пакетів і мовчки
        # падає з «could not determine executable to run».
        Step 'Типи клієнта' { & npm.cmd run typecheck }
        Step 'Стиль клієнта' { & npm.cmd run lint }
        Step 'Тести клієнта' { & npm.cmd run test }
        Step 'Збірка клієнта' { & npm.cmd run build }
    }
    finally {
        Pop-Location
    }
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "Невдалих перевірок: $($failures.Count)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  • $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Усі перевірки пройдено.' -ForegroundColor Green
