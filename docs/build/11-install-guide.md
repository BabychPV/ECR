# Інструкція: встановлення, оновлення, перевстановлення ECR

> Практичний посібник для того, хто РОЗГОРТАЄ систему на сервері —
> адміністратора, не обов'язково розробника. Архітектурні рішення й
> внутрішня механіка MSI/WiX — у `docs/build/10-installer.md`; тут лише
> "що ввести в консоль і в якому порядку".
>
> Усі команди — PowerShell, виконувати на **Windows Server 2012 R2+**
> (x64) з правами адміністратора.

## Зміст

- [0. Що потрібно заздалегідь](#0-передумови)
- [1. Збірка інсталятора (на машині розробки)](#1-збірка)
- [2. Перше встановлення на чистому сервері](#2-перше-встановлення)
- [3. Перший вхід у систему](#3-перший-вхід)
- [4. Оновлення на нову версію](#4-оновлення)
- [5. Перевстановлення / відновлення](#5-перевстановлення)
- [6. Видалення](#6-видалення)
- [7. Де шукати, коли щось не так](#7-діагностика)
- [8. Типові помилки та їх причини](#8-типові-помилки)
- [9. Ручні операції з реєстром служби (без deploy-ecr.ps1)](#9-ручні-операції)

---

## 0. Передумови

**На сервері, де встановлюємо:**
- Windows Server 2012 R2 або новіший, x64.
- SQL Server (Express достатньо), **база вже створена заздалегідь**
  адміністратором БД — інсталятор і `deploy-ecr.ps1` базу НЕ створюють
  і не видаляють (`docs/build/10-installer.md` §1.3).
- Порт для Kestrel (типово `5000`) вільний.

**На машині, де ЗБИРАЄМО `.msi`** (може бути та сама машина, може бути
інша — окремої "збірної" машини не вимагається):
- .NET SDK (версія — за `global.json` репозиторію).
- WiX CLI: `dotnet tool install --global wix --version 5.0.2`
  (саме 5.0.2, не 6+ — `docs/build/10-installer.md` §1.1, платна
  Maintenance Fee з квітня 2025).
- Node.js + npm (для збірки `src/Ecr.Web`).
- Клон цього репозиторію.

⚠ **Дві РІЗНІ машини можуть знадобитись**, якщо на сервері немає й не
буде .NET SDK/Node — сервер отримує вже готовий `.msi`, більше нічого.

---

## 1. Збірка інсталятора {#1-збірка}

З кореня репозиторію:

```powershell
.\tools\rebuild-and-package-msi.ps1 -Version 1.0.0
```

Це послідовно: перебудовує `Ecr.Api`, збирає `src/Ecr.Web` (npm ci +
npm run build), пакує все в `.msi`. Готовий файл —
`artifacts\msi\en-US\Ecr.msi` (~65 МБ, включно з веб-клієнтом).

Якщо потрібен лише сам `.msi` без перебудови .NET-коду (наприклад,
міняли лише щось у `src/Ecr.Web`):

```powershell
.\tools\build-msi.ps1 -Version 1.0.0 -SkipPublish
```

Прапорець `-SkipWeb` — навпаки, зібрати MSI БЕЗ веб-клієнта (лишає
попередню поведінку, коли вебки не було; здебільшого не потрібен).

⛔ **Скопіювати `Ecr.msi` на сервер** (мережева тека, USB, RDP-буфер —
що зручно). Далі всі команди — вже на сервері.

---

## 2. Перше встановлення на чистому сервері {#2-перше-встановлення}

### 2.1 Рекомендований шлях — один виклик

На сервері, з правами адміністратора, у теці, куди скопійовано
`Ecr.msi` і звідки видно репозиторій (або принаймні `tools\deploy-ecr.ps1`):

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

$cs = Read-Host -AsSecureString -Prompt 'Рядок підключення до SQL Server'
$bp = Read-Host -AsSecureString -Prompt 'Пароль bootstrap-адміністратора (задай сам і запиши)'

.\tools\deploy-ecr.ps1 `
    -SqlInstance 'ІМ''Я_СЕРВЕРА\SQLEXPRESS' `
    -Database 'ECR' `
    -MsiPath '.\Ecr.msi' `
    -ConnectionString $cs `
    -BootstrapPassword $bp `
    -FirstDeployment
```

⛔ **Викликати САМЕ так** (`.\deploy-ecr.ps1 ...`), а НЕ
`pwsh -File .\deploy-ecr.ps1 ...` і не через новий процес: `-ConnectionString`/
`-BootstrapPassword`/`-ServicePassword` — `SecureString`, а `SecureString`
не переживає перехід у ІНШИЙ процес (значення просто зіпсується і впаде
з помилкою конвертації типу). У ТІЙ САМІЙ сесії PowerShell, без `-File`
— обов'язково.

**Що робить цей один виклик** (детально — `docs/build/10-installer.md`
§10): перевіряє передумови → накочує схему БД → встановлює MSI → пише
секрети в реєстр служби → пише нЕсекретну конфігурацію → (лише якщо
задано `-ServiceAccount`, розділ 2.2) стартує службу й чекає, поки
`/health/live` відповість.

⚠ **Без `-ServiceAccount` (як у прикладі вище) служба реєструється, але
СВІДОМО не стартує сама** (`docs/build/10-installer.md` §1.4) — і
скрипт ЗАКОНОМІРНО завершиться помилкою на останньому кроці
(«Служба не відповіла за відведений час»): він чесно чекає на
`/health/live` до кінця, а стартувати службу без облікового запису
свідомо не буде. Це не збій установки — усі попередні 6 кроків уже
відпрацювали. Далі вручну:

```powershell
Start-Service EcrApi
Invoke-WebRequest http://localhost:5000/health/live -UseBasicParsing
```

Якщо потрібно, щоб `deploy-ecr.ps1` сам довів справу до працюючої
служби за один виклик, без ручного `Start-Service` — дивись 2.2.

### 2.2 Якщо служба має працювати під окремим обліковим записом

Додати до виклику вище:

```powershell
-ServiceAccount 'DOMAIN\ecr-svc$'          # gMSA -- рекомендовано, без пароля
```

або, якщо звичайний (не gMSA) обліковий запис:

```powershell
$svcPass = Read-Host -AsSecureString -Prompt 'Пароль облікового запису служби'
... -ServiceAccount 'DOMAIN\ecr-svc' -ServicePassword $svcPass
```

Без `-ServiceAccount` служба піднімається під `LocalSystem` — цього
достатньо для тестового/внутрішнього контуру, якщо `LocalSystem` має
доступ до SQL Server (Windows-автентифікація комп'ютера).

### 2.3 Побачити план, нічого не роблячи в системі

Додати `-WhatIf` до будь-якого виклику вище — жоден `sqlcmd`, `msiexec`
чи запис у реєстр не виконається, лише друк того, що ВІДБУЛОСЬ Б.

---

## 3. Перший вхід у систему {#3-перший-вхід}

Тут вважаємо, що служба вже `Running` (розділ 2 — сама, якщо задавали
`-ServiceAccount`, або вручну через `Start-Service EcrApi`, якщо ні) і
`/health/live` відповідає. Автентифікація — **не лише** через
Windows-обліковий запис: є звичайна форма логін/пароль, і саме нею
треба скористатись першого разу.

1. Відкрити `http://<сервер>:5000/` (або інший `-AppPort`, якщо
   задавали інший) — це вже РЕАЛЬНИЙ інтерфейс застосунку, не документація API.
2. Форма входу → **не** кнопка "Windows" — звичайний логін/пароль:
   - логін: `bootstrap`
   - пароль: той, що ввели в `-BootstrapPassword` на кроці 2.1
3. Застосунок одразу вимагає змінити пароль — це очікувано.
4. Обліковий запис `bootstrap` має право `Security.ManageUsers`. Через
   нього — видати права РЕАЛЬНОМУ (домен- чи локальному) користувачу,
   під яким люди будуть заходити щодня.
5. Щойно якийсь інший користувач отримає право `Security.ManageUsers`,
   `bootstrap` автоматично деактивується сам — видаляти вручну не треба.
6. **Прибрати пароль bootstrap із реєстру служби** (він лишається там,
   поки не прибрати явно — розділ 9 нижче) і перезапустити службу.

---

## 4. Оновлення на нову версію {#4-оновлення}

Зібрати новий `.msi` з БІЛЬШИМ номером версії (крок 1), скопіювати на
сервер, і:

```powershell
.\tools\deploy-ecr.ps1 `
    -SqlInstance 'ІМ''Я_СЕРВЕРА\SQLEXPRESS' -Database 'ECR' `
    -MsiPath '.\Ecr.msi' -ConnectionString $cs
```

(без `-BootstrapPassword` і без `-FirstDeployment` — це не перше
розгортання; `-ConnectionString` усе одно потрібен, бо крок 4/7
записує його щоразу — значення не змінюється, якщо рядок той самий).

`MajorUpgrade` сам знімає стару версію й ставить нову
(`docs/build/10-installer.md` §1.5) — простою бути не мало б, окрім
короткої паузи на рестарт служби. Конфігурація в `%ProgramData%\ECR\config\`
і логи в `%ProgramData%\ECR\logs\` **переживають** оновлення
(`NeverOverwrite` — той самий файл, що адміністратор, можливо, вже
відредагував, оновлення його не чіпає).

Якщо MSI будували БЕЗ `-MsiPath` (тобто `deploy-ecr.ps1` сам викликав
`build-msi.ps1`) — достатньо `-Version` замість `-MsiPath`.

⚠ **Без `-ServiceAccount` (розділ 2.2) MSI зупиняє службу на час
оновлення БЕЗУМОВНО, а запускає її знову НАЗАД лише за умови заданого
`-ServiceAccount`.** Якщо перше встановлення робили без нього (сервіс
підняли вручну, розділ 2.1) і оновлення робиться так само без нього —
після оновлення служба лишиться `Stopped`, і це не збій:
`Start-Service EcrApi` після кожного такого оновлення — очікувана дія,
не діагностика.

---

## 5. Перевстановлення / відновлення {#5-перевстановлення}

**Файл пошкоджено чи службу знесли вручну, версія та сама:**

```powershell
msiexec /f Ecr.msi /qn /l*v repair.log
```

**Хочеться почати з чистого аркуша на цій самій базі** (рідко потрібно
— спершу `/f` вище):

```powershell
msiexec /x Ecr.msi /qn
# ... потім секція 2 заново, БЕЗ -FirstDeployment (схема вже накочена,
# 14-agent-jobs.sql удруге не потрібен)
```

⛔ Видалення MSI **не чіпає** базу даних і **не чіпає** `%ProgramData%\ECR\`
(логи й конфіг лишаються на диску — прибирає адміністратор вручну, якщо
вважає за потрібне).

---

## 6. Видалення {#6-видалення}

```powershell
msiexec /x Ecr.msi /qn /l*v uninstall.log
# або за ProductCode, якщо самого .msi під рукою немає:
msiexec /x {ProductCode} /qn
```

Служба зупиняється й прибирається, файли з `Program Files\ECR\` —
теж. База даних, `%ProgramData%\ECR\logs\` і
`%ProgramData%\ECR\config\` — **лишаються**, видаляти окремо, якщо
потрібно.

---

## 7. Де шукати, коли щось не так {#7-діагностика}

| Що | Де |
|---|---|
| Лог самої установки MSI | `ecr-install.log` (поруч, куди вказано `/l*v`) |
| Лог застосунку (Serilog/файловий) | `%ProgramData%\ECR\logs\` |
| Системний Event Log | Джерело `ECR`, канал `Application` |
| Чи стартувала служба | `Get-Service EcrApi` |
| Чи відповідає застосунок | `http://localhost:<APP_PORT>/health/live` |
| Детальний стан БД/партицій | `http://localhost:<APP_PORT>/health/db` |
| Змінні оточення служби (секрети) | `Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi' -Name Environment` |

Найшвидша перевірка "чи взагалі щось не так із самим застосунком, а не
з інсталятором": зупинити службу й запустити `.exe` напряму, побачити
консоль наживо:

```powershell
Stop-Service EcrApi
& "C:\Program Files\ECR\Api\Ecr.Api.exe"
# Ctrl+C, потім Start-Service EcrApi, коли розібрались
```

---

## 8. Типові помилки та їх причини {#8-типові-помилки}

Список — з РЕАЛЬНИХ прогонів, не з документації Microsoft.

| Симптом | Причина | Що робити |
|---|---|---|
| `Error 1920` при встановленні | Служба намагається стартувати одразу, але немає доступу до SQL (типово — під `LocalSystem`) | Задати `-ServiceAccount` з правами на БД, або дати `LocalSystem`/`NT AUTHORITY\SYSTEM` доступ у SQL Server |
| `Error 1923` | Заданий `-ServiceAccount` БЕЗ пароля (і це не gMSA) | Додати `-ServicePassword`, або використати gMSA (без пароля) |
| `0x80070534` / `ERROR_NONE_MAPPED` про обліковий запис | Ім'я облікового запису не резолвиться в SID (типово — локальний профіль з міткою Microsoft-акаунта) | Використати домен-обліковий запис або `LocalSystem` замість особистого логіну |
| `Error 1053` (служба не відповіла вчасно) — вже ПІСЛЯ успішної установки | Сам процес `Ecr.Api.exe` падає/висне при старті — інсталятор тут ні до чого | Розділ 7: запустити `.exe` напряму, побачити справжню помилку |
| `InvalidOperationException`: рядок підключення не заданий | `-ConnectionString` не передавали, або він не потрапив у реєстр служби | Розділ 9 нижче — задати вручну, або перезапустити `deploy-ecr.ps1` з `-ConnectionString` |
| Форма входу малює `⟦...⟧` замість тексту | Каталог UI-рядків не завантажився (мережа/API), а не помилка перекладу | Перевірити `/api/v1/ui-strings` (чи що є фактичним шляхом), дивитись мережеву вкладку браузера |
| `npm : cannot be loaded because running scripts is disabled` | Execution Policy | `Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force`, або викликати `npm.cmd` замість `npm` |
| Кракозябри в помилках `.ps1`-скриптів | Стара PowerShell 5.1 читає кириличний `.ps1` без UTF-8 BOM у системній кодовій сторінці | Скрипти цього дерева вже мають BOM; якщо власний скрипт — зберегти як UTF-8 **з BOM** |
| `Cannot convert ... to SecureString` при виклику з `-ConnectionString $cs` | Викликали через `pwsh -File script.ps1 -Param $secureVar` — це НОВИЙ процес, `SecureString` не переживає межу процесів | Викликати `.\deploy-ecr.ps1 ...` напряму в тій самій сесії, без `-File` |
| `The property 'Statement' cannot be found` під час `npm ci`/`npm run build` | Власний `npm.ps1` несумісний зі `Set-StrictMode -Version Latest` | Викликати `npm.cmd` замість голого `npm` |
| `sqlcmd`: не може підключитись до `localhost` | SQL Server Express встановлюється як ІМЕНОВАНИЙ екземпляр | `<ІмяКомп'ютера>\SQLEXPRESS`, не голий `localhost` |
| `CS2012`: файл `.pdb` зайнятий | Одночасна/перервана збірка лишила процес `VBCSCompiler.exe` з відкритим файлом | `dotnet build-server shutdown`, за потреби `Stop-Process` на залишених `VBCSCompiler`/`dotnet` |
| `npm error EPERM ... unlink ... esbuild.exe` | Запущений `npm run dev` тримає файл у `node_modules` | Зупинити dev-сервер (`Stop-Process` на `node`/`esbuild`) перед `npm ci` |

---

## 9. Ручні операції з реєстром служби (без deploy-ecr.ps1) {#9-ручні-операції}

Секрети служби (`ECR_ConnectionStrings__Ecr`, `ECR_Bootstrap__Password`)
живуть у `HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment`
(REG_MULTI_SZ) — той самий канал, яким сам Windows SCM передає оточення
процесу служби. `deploy-ecr.ps1` робить це автоматично; нижче — те саме
вручну, якщо оркестратор недоступний або треба точковий фікс.

**Задати/замінити один запис, не займаючи інші:**

```powershell
$name  = 'ECR_ConnectionStrings__Ecr'
$value = 'Server=<ІМ''Я>\SQLEXPRESS;Database=ECR;Trusted_Connection=True;TrustServerCertificate=True'

$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi'
$existing = (Get-ItemProperty -Path $key -Name Environment -ErrorAction SilentlyContinue).Environment
$updated = @($existing | Where-Object { $_ -notlike "$name=*" }) + "$name=$value"
Set-ItemProperty -Path $key -Name Environment -Value $updated -Type MultiString

Restart-Service EcrApi
```

**Прибрати один запис** (наприклад, `ECR_Bootstrap__Password` після
першого входу):

```powershell
$name = 'ECR_Bootstrap__Password'
$key  = 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi'
$existing = (Get-ItemProperty -Path $key -Name Environment -ErrorAction SilentlyContinue).Environment
$updated = @($existing | Where-Object { $_ -notlike "$name=*" })
Set-ItemProperty -Path $key -Name Environment -Value $updated -Type MultiString

Restart-Service EcrApi
```

⛔ **Секрети НІКОЛИ не пишуться** в
`%ProgramData%\ECR\config\appsettings.Production.json` чи будь-який
інший файл `appsettings*.json` (D-11, `docs/build/04-environment.md`
§6) — лише в реєстр служби, як вище. Цей JSON-файл — ЛИШЕ для
несекретних налаштувань майданчика (наприклад, `Telemetry:OtlpEndpoint`).
