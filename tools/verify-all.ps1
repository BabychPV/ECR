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

# ⛔ Запущений застосунок тримає `Ecr.Infrastructure.dll`, і складання падає з
# MSB3027 — «файл використовується іншим процесом». Помилка виглядає як
# зламаний код, а насправді це забутий `dotnet run` у сусідньому вікні.
#
# ⚠ Знайдено аудитом: прогін показав ✗ на кроці складання при цілком
# справному дереві, і на з'ясування причини пішло більше часу, ніж на цей
# рядок.
Get-Process -Name 'Ecr.Api' -ErrorAction SilentlyContinue | Stop-Process -Force

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
        # ⛔ Гейт безпеки (D-143). Перевіряються ЛИШЕ ті залежності, що
        # потрапляють до користувача: `--omit=dev`. Вразливість у
        # `react-router-dom` 7.1.1 (XSS через відкриті редіректи в `<Link>`)
        # знайшлася вручну — далі це робить машина.
        #
        # ⚠ Для dev-залежностей гейт НЕ ставиться: він блокував би роботу
        # через чужі релізи, і його вимкнули б разом із робочим. Їхній стан
        # виводиться довідково нижче.
        Step 'Безпека залежностей' { & npm.cmd audit --omit=dev --audit-level=high }

        Step 'Аудит інструментів (довідково)' {
            & npm.cmd audit
            # ⚠ Ненульовий код тут НЕ є помилкою: це довідка, а не гейт.
            $global:LASTEXITCODE = 0
        }

        # ⛔ Типи клієнта РЕГЕНЕРУЮТЬСЯ зі знімка контракту і мають збігтися з
        # тим, що в репозиторії (D-137). Розбіжність означає, що знімок
        # оновили, а типи — ні: рівно той стан, у якому клієнт описує форму
        # відповіді сам і розходиться з сервером мовчки (`A7-36`).
        Step 'Типи клієнта зі знімка' {
            & npm.cmd run api:types
            if ($LASTEXITCODE -ne 0) { return }

            & git -C $root diff --exit-code -- 'src/Ecr.Web/src/api/schema.d.ts'
            if ($LASTEXITCODE -ne 0) {
                throw 'schema.d.ts розійшовся зі знімком OpenAPI — перегенеруй і закоміть.'
            }
        }

        Step 'Типи клієнта' { & npm.cmd run typecheck }
        Step 'Стиль клієнта' { & npm.cmd run lint }
        Step 'Тести клієнта' { & npm.cmd run test }

        # ⛔ Доступність — окремим кроком, бо повільна: `axe` у jsdom обробляє
        # одну сторінку близько 35 секунд. Усередині звичайного `npm test` це
        # додавало б хвилини до кожного прогону під час роботи, і перевірку
        # зрештою вимкнули б. Поріг блокуючий: нуль `critical` і `serious`
        # на КОЖНОМУ маршруті (ФВ-14.16, D-127).
        Step 'Доступність клієнта' { & npm.cmd run test:a11y }
        Step 'Збірка клієнта' { & npm.cmd run build }

        # ⛔ Гейт бюджету (`D-132`). До нього бюджет був записаний у двох
        # документах і не перевірявся ніде: `07-checkpoints.md` стверджував,
        # що його стереже `npm run build`, а той друкує розміри і виходить із
        # нулем незалежно від них. Обґрунтування самого рішення — «бюджет,
        # який не перевіряють, не існує» — описувало власний стан.
        #
        # ⚠ Обов'язково ПІСЛЯ збірки: гейт зважує `dist/`, а не вихідний код.
        Step 'Бюджет клієнта' { & npm.cmd run budget }
    }
    finally {
        Pop-Location
    }
}

if (-not $SkipClient -and -not $SkipDeployment) {
    # ⛔ Прогони у СПРАВЖНЬОМУ браузері — останніми і найдорожчими
    # (близько чотирьох хвилин). Три речі з ЕТАПУ 7.5 неможливі в jsdom, бо
    # в ньому немає ані розкладки, ані пікселів:
    #   — структурна різниця станів комірки після знеколірення (`D-140`);
    #   — прохід оператора без миші з виміром кільця фокуса (`D-141`);
    #   — знімки маршрутів під двома ролями × темами × щільностями (`D-142`).
    #
    # ⚠ Стенд піднімає власну базу і власний застосунок і видаляє їх за
    # собою. Без нього прогони, які потребують входу, мовчки пропускаються
    # (`test.skip`) — а мовчазний пропуск і є те, що ЕТАП 7.5 виловлює.
    Step 'Прогони в браузері' {
        & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'e2e-stand.ps1')
    }
}

Write-Host ''

if ($failures.Count -gt 0) {
    Write-Host "Невдалих перевірок: $($failures.Count)" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  • $_" -ForegroundColor Red }
    exit 1
}

Write-Host 'Усі перевірки пройдено.' -ForegroundColor Green
