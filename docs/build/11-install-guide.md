# Інструкція: встановлення, оновлення, перевстановлення ECR

> Практичний посібник для того, хто РОЗГОРТАЄ систему на сервері —
> адміністратора, не обов'язково розробника. Архітектурні рішення й
> внутрішня механіка MSI/WiX — у `docs/build/10-installer.md`; тут лише
> "що ввести/натиснути і в якому порядку".
>
> **Два шляхи, той самий результат:** самодостатній `Ecr-Setup-<версія>.exe`
> (розділ 2.1, рекомендовано — п'ять екранів, РІВНО один файл на сервері)
> або `deploy-ecr.ps1` напряму (розділ 2.2 — для автоматизації/CI). Розділи
> 3–9 однакові для обох.
>
> Усі команди — PowerShell, виконувати на **Windows Server 2012 R2+**
> (x64) з правами адміністратора.

## Зміст

- [0. Що потрібно заздалегідь](#0-передумови)
- [1. Збірка інсталятора (на машині розробки)](#1-збірка)
- [2. Перше встановлення на чистому сервері](#2-перше-встановлення)
  - [2.1 Рекомендований шлях — самодостатній `Ecr-Setup-<версія>.exe`](#2-1-майстер)
  - [2.2 Альтернатива — `deploy-ecr.ps1` напряму (автоматизація/CI)](#2-2-скрипт)
  - [2.3 Якщо служба має працювати під окремим обліковим записом](#2-3-обліковий-запис)
  - [2.4 Побачити план, нічого не роблячи в системі](#2-4-whatif)
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
- **Більше нічого.** `Ecr-Setup-<версія>.exe` (розділ 1, 2.1) — self-contained
  single-file: жодного .NET SDK чи Runtime, жодного Node, жодного
  `dotnet-ef`, жодного клону репозиторію на сервері не потрібно (`Q-219`).

**На машині, де ЗБИРАЄМО інсталятор** (може бути та сама машина, може
бути інша — окремої "збірної" машини не вимагається):
- .NET SDK (версія — за `global.json` репозиторію).
- WiX CLI: `dotnet tool install --global wix --version 5.0.2`
  (саме 5.0.2, не 6+ — `docs/build/10-installer.md` §1.1, платна
  Maintenance Fee з квітня 2025).
- Node.js + npm (для збірки `src/Ecr.Web`).
- `dotnet-ef`: `dotnet tool install --global dotnet-ef --version <версія
  Microsoft.EntityFrameworkCore.Design з Directory.Packages.props>`
  (генерує `migration.sql` заздалегідь — саме тому сервер потім його не
  потребує).
- Клон цього репозиторію.

⚠ **Дві РІЗНІ машини можуть знадобитись**, якщо на сервері немає й не
буде нічого з переліку вище — сервер отримує РІВНО один готовий `.exe`,
більше нічого.

---

## 1. Збірка інсталятора {#1-збірка}

**Рекомендовано — один файл на все.** З кореня репозиторію:

```powershell
.\tools\build-installer.ps1 -Version 1.0.0
```

Один виклик: збирає `Ecr.msi` (build-msi.ps1 — перебудова `Ecr.Api`,
веб-клієнт, пакування), генерує `migration.sql` (`dotnet ef migrations
script`, ТУТ, не на сервері), і публікує `Ecr-Setup-<версія>.exe` —
self-contained, single-file, з усім необхідним (Ecr.msi, deploy-ecr.ps1,
migration.sql, сирі SQL-скрипти) вбудованим У СЕРЕДИНУ самого файлу
(.NET `IncludeAllContentForSelfExtract` розпаковує це в тимчасову теку
щоразу при запуску — код майстра цього навіть не помічає, бо й так читає
`AppContext.BaseDirectory`). Готовий файл — `artifacts\installer\
Ecr-Setup-1.0.0.exe` (орієнтовно 150–250 МБ: self-contained рантайм +
`Ecr.msi` з веб-клієнтом усередині).

⛔ **Скопіювати на сервер: РІВНО цей один файл.** Жодної теки, жодного
другого файлу поруч. Так було не завжди (`Q-219`) — раніше треба було
тримати `EcrSetup.exe` + `deploy-ecr.ps1` + `Ecr.msi` разом, а
`deploy-ecr.ps1` без `-SkipSchema` додатково вимагав `src/Ecr.
Infrastructure` і `dotnet-ef`/.NET SDK на самому сервері (розрив,
знайдений при прямому питанні "які файли мають бути поруч").

**Розкладений варіант** (менший файл, потрібен лише коли `Ecr-Setup-*.exe`
з якоїсь причини не годиться — наприклад, обмеження на розмір
файлу, що передається) — той самий набір кроків окремо:

```powershell
.\tools\rebuild-and-package-msi.ps1 -Version 1.0.0
dotnet publish tools\Ecr.Setup\Ecr.Setup.csproj -c Release -o artifacts\setup
copy tools\deploy-ecr.ps1 artifacts\setup\
```

Тоді на сервер їдуть `EcrSetup.exe` + `deploy-ecr.ps1` (з `artifacts\
setup\`) + `Ecr.msi` (з `artifacts\msi\en-US\`) — усі три в ОДНУ теку
(`EcrSetup.exe` шукає `.msi`/`deploy-ecr.ps1` **поруч із собою**,
`InstallStep.ResolveScriptPath`/`AccountAndNetworkStep.TryDetectMsi`).
**Але:** без `-SkipSchema` `deploy-ecr.ps1` у цьому варіанті все одно
шукає `src/Ecr.Infrastructure` (`dotnet-ef`) відносно СЕБЕ — тобто або
ставити з `-SkipSchema` (DBA вже накотив схему окремо), або тримати на
сервері й клон репозиторію з SDK. Саме цього розкладений варіант і не
вирішує — для нього і зроблено `build-installer.ps1` вище.

Прапорці `-SkipPublish`/`-SkipWeb`, якщо потрібні лише частково —
дивись `build-msi.ps1`/`rebuild-and-package-msi.ps1` напряму;
`build-installer.ps1` пакує "усе в одному" й таких прапорців не приймає.

---

## 2. Перше встановлення на чистому сервері {#2-перше-встановлення}

### 2.1 Рекомендований шлях — майстер `EcrSetup.exe` {#2-1-майстер}

Запустити скопійований на сервер `Ecr-Setup-<версія>.exe` (той самий
майстер, лише self-contained і перейменований `build-installer.ps1`;
розкладений варіант розділу 1 — просто `EcrSetup.exe`) **від імені
адміністратора** (маніфест вимагає це сам — Windows перепитає, якщо
забув). П'ять екранів по черзі, одне питання за раз:

1. **Deployment Mode** — «First deployment» чи «Update an existing
   installation».
2. **Service Account and Network** — обліковий запис служби (Local System /
   gMSA / обліковий запис і пароль), порт, файл `Ecr.msi` (майстер сам
   пропонує `.msi`, що лежить поруч, якщо такий є).
3. **Database** — SQL Server, назва бази, автентифікація
   (Windows або SQL-логін).
4. **Administrator Password** — лише в режимі «First deployment»;
   у режимі «Update» цей екран пропускається сам.
5. **Review** — підсумок усього вище, **без жодного значення пароля**
   (лише «provided»/«not provided») — останній екран перед реальними
   діями, кнопка тут підписана «Install».

Далі — жива панель виконання: чеклист із семи кроків того самого
`deploy-ecr.ps1` (розділ 2.2 нижче — майстер його й викликає, нічого не
дублює; назви кроків у чеклисті — англійською, бо це власний текст
майстра, а не вивід скрипта), і розгортка «Show detailed log» для
повного виводу мовою самого `deploy-ecr.ps1` (українською), якщо щось
пішло не так. У кінці — «Finish» або «Try Again» із поверненням на
екран огляду.

⚠ **Мова:** інтерфейс майстра (написи, кнопки, повідомлення про
помилки) — англійською. «Detailed log» показує сирий вивід
`deploy-ecr.ps1` як є, і той лишається українською — переклад
внутрішніх повідомлень скрипта в цю зміну не входив.

⚠ **Секрети (пароль SQL-логіну, пароль облікового запису служби, пароль
адміністратора) ідуть у `deploy-ecr.ps1` напряму як об'єкти `.NET
SecureString`** — майстер хостить PowerShell **у своєму процесі**
(`Microsoft.PowerShell.SDK`), а не запускає `pwsh.exe` окремо, тож
пастка з розділу 2.2/8 нижче (`SecureString` не переживає межу процесів)
тут узагалі не виникає.

Розділи 3–9 нижче однакові незалежно від того, яким шляхом
поставили — 2.1 чи 2.2.

### 2.2 Альтернатива — `deploy-ecr.ps1` напряму (автоматизація/CI) {#2-2-скрипт}

Той самий результат без GUI — для CI/CD, автоматизованого розгортання
чи якщо `EcrSetup.exe` з якоїсь причини недоступний. На сервері, з
правами адміністратора, у теці, куди скопійовано `Ecr.msi` і звідки
видно репозиторій (або принаймні `tools\deploy-ecr.ps1`):

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

### 2.3 Якщо служба має працювати під окремим обліковим записом {#2-3-обліковий-запис}

Через майстер (2.1) — поле «Обліковий запис служби» на екрані 2. Через
`deploy-ecr.ps1` напряму (2.2) — додати до виклику вище:

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

### 2.4 Побачити план, нічого не роблячи в системі {#2-4-whatif}

Лише для `deploy-ecr.ps1` напряму (2.2) — майстер (2.1) такого режиму
поки не має (окремий пункт на майбутнє, не забутий кут: людина, що веде
майстер, і так бачить кожен крок на екрані 5 перед «Встановити»).

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

⛔ **Прибирати пароль bootstrap вручну не треба** (`Q-215`): він ніколи
не потрапляє в реєстр служби — застосунок сам читає його з одноразового
файлу (`%ProgramData%\ECR\config\bootstrap.secret`) при першому старті
й одразу видаляє файл, незалежно від результату. Якщо файл усе ж
лишився на диску після успішного першого входу — це означає, що
видалення не вдалося (перевір Event Log, джерело `ECR`, на попередження
"не вдалося видалити одноразовий файл"), і тоді прибрати його вручну
дійсно потрібно — але це відхилення від норми, не штатний крок.

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
| Рядок підключення (постійний секрет) | `Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi' -Name Environment` |
| Одноразовий файл bootstrap-пароля (має зникнути після першого входу — `Q-215`) | `Test-Path '%ProgramData%\ECR\config\bootstrap.secret'` |

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

⚠ **Це розділ про рядок підключення (`ECR_ConnectionStrings__Ecr`) —
постійний секрет, потрібний службі щоразу при старті.** Bootstrap-пароль
(`Q-215`) сюди більше **не** належить: він живе одноразовим файлом
(`%ProgramData%\ECR\config\bootstrap.secret`), не реєстром, і застосунок
прибирає його сам — див. розділ 3 вище. Якщо потрібно задати bootstrap-
пароль вручну, без `deploy-ecr.ps1 -BootstrapPassword` і без майстра:

```powershell
Set-Content -Path '%ProgramData%\ECR\config\bootstrap.secret' `
    -Value '<пароль тут>' -Encoding UTF8 -NoNewline
# ACL звужити на обліковий запис служби (Read, Delete) вручну —
# icacls, чи Set-Acl тим самим прийомом, що Set-BootstrapSecretFile
# у tools/deploy-ecr.ps1 — інакше файл лишається читабельним ширше,
# ніж треба.
Restart-Service EcrApi
```

Рядок підключення живе у
`HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi\Environment`
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

**Прибрати один запис** (загальна форма — рядок підключення міняється
рідко, але буває):

```powershell
$name = 'ECR_ConnectionStrings__Ecr'
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
