# HTTPS під один сертифікат із SAN

✎ 2026-10-01 (`D-259`, `D14-08`, `R-01`, черга `CL-2`). Відповідь людини (3.5.3,
`QUESTIONS-BUSINESS-2026-10-01`): «замовник надасть один сертифікат в якому буде SAN».
Хто завершує TLS (`Q-5`) не підтверджено; дефолт — **сам застосунок** (Kestrel із цим
сертифікатом, `deploy-ecr.ps1 -HttpsThumbprint`). Режим проксі — розділ 6.

Документ описує те, що **є** в коді й скриптах: `tools/deploy-ecr.ps1`,
`src/Ecr.Api/Startup/HttpsTransport.cs`, `src/Ecr.Api/Auth/AuthenticationSetup.cs`
(Data Protection), майстер `tools/Ecr.Setup` (`TransportStep`), MSI
(`installer/Ecr.Installer/Service.wxs`). Довідник усіх трьох транспортів —
`docs/build/11-install-guide.md` §2.7, експлуатація — `operations-runbook.md` §11. Тут —
покроковий шлях для одного випадку: **один сертифікат замовника з SAN**.

⚠ Команди нижче в цьому лейні **не прогнано** на Windows-стенді (хмарний контейнер без
Windows). Розділ 10 — сценарій перевірки на стенді із самопідписаним сертифікатом; до
його прогону команди мають статус «звірені з кодом, не виконані».

## Зміст

1. [Що застосунок робить із сертифікатом](#1)
2. [Вимоги до сертифіката (що попросити в замовника)](#2)
3. [Один сертифікат і для HTTPS, і для Data Protection?](#3)
4. [Покроково: встановлення з HTTPS](#4)
5. [Перенаправлення порту http → https](#5)
6. [Режим `-BehindHttpsProxy`](#6)
7. [Data Protection: ключі й сертифікат](#7)
8. [Перевірка після встановлення](#8)
9. [Заміна сертифіката](#9)
10. [Перевірка на стенді із самопідписаним сертифікатом](#10)
11. [Типові збої](#11)
12. [Відомі розбіжності](#12)

---

## 1. Що застосунок робить із сертифікатом {#1}

Два незалежні параметри, кожен — відбиток сертифіката в `Cert:\LocalMachine\My`:

| Параметр `deploy-ecr.ps1` | Змінна служби `EcrApi` | Для чого | Без нього |
|---|---|---|---|
| `-HttpsThumbprint` | `ECR_Transport__Https__CertificateThumbprint` | TLS: Kestrel віддає цей сертифікат браузеру | треба `-BehindHttpsProxy` або `-AllowHttp` (стенд), інакше скрипт зупиняється на кроці 1 |
| `-DataProtectionThumbprint` | `ECR_Auth__DataProtection__CertificateThumbprint` | шифрує ключі кільця Data Protection у `sec.DataProtectionKey` (cookie сеансу, секрети каналів сповіщень) | обов'язковий (S11): служба в Production не стартує |

- Сертифікат шукається **за відбитком** (не за `Subject`) — так однозначно, коли в сховищі
  лежать старий і новий сертифікати одного імені (`HttpsTransport.cs`, коментар до класу).
- Відбиток можна копіювати з вікна сертифіката Windows із пробілами — і скрипт
  (`ConvertTo-NormalizedThumbprint`), і застосунок (`HttpsTransport.Normalize`) прибирають
  пробіли й нерозривні пробіли.
- Сертифікат HTTPS діє для **кожної** `https://`-адреси з `ASPNETCORE_URLS`
  (`ConfigureHttpsDefaults`); окремої секції `Kestrel:Endpoints` немає.
- Служба `EcrWorker` сертифікатів не використовує: Data Protection і HTTP є лише в `EcrApi`
  (`AddDataProtection` викликається тільки в `src/Ecr.Api`).
- `netsh http add sslcert` / `urlacl` **не потрібні**: це Kestrel, а не HTTP.sys.

## 2. Вимоги до сертифіката (що попросити в замовника) {#2}

| # | Вимога | Чому / що перевіряє код |
|---|---|---|
| 1 | **PFX із закритим ключем** (не `.cer`) | скрипт на кроці 1 і застосунок на старті відмовляють, якщо `HasPrivateKey = False` |
| 2 | **SAN містить кожне ім'я, за яким відкриватимуть застосунок** — FQDN аліасу (`ecr.customer.local`) і, за потреби, FQDN кожного вузла | ні скрипт, ні застосунок SAN не перевіряють; розбіжність — попередження браузера на кожному робочому місці |
| 3 | **EKU містить Server Authentication** (`1.3.6.1.5.5.7.3.1`) або EKU немає | Kestrel не бере сертифікат без нього — служба не стартує (`operations-runbook.md` §5). Скрипт EKU не перевіряє (розділ 12) |
| 4 | **Ключ RSA** (2048+), якщо той самий сертифікат піде на Data Protection | Data Protection шифрує ключі через `EncryptedXml` — він працює лише з RSA. Для HTTPS годиться й ECDSA (розділ 3) |
| 5 | **Ключ експортований** (`Exportable`), якщо вузлів кілька або сертифікат піде на Data Protection | один і той самий сертифікат Data Protection має стояти на всіх вузлах (D-32) |
| 6 | Ланцюг: проміжні ЦС — у сервері, корінь — у довірених на робочих місцях (зазвичай GPO замовника) | інакше браузер не довіряє |
| 7 | Строк — щонайменше до наступного планового вікна | менше 30 днів — попередження скрипта, жовтий `transport` на `/health/ready`; прострочений старт **не** зупиняє |

⚠ Невідомо (питання до ІБ замовника, у `docs/` відповіді немає): точне ім'я хоста (FQDN
аліасу), ЦС, скільки вузлів, чи ключ експортований. Нейтральний дефолт для прикладів —
`ecr.customer.local`, один вузол.

## 3. Один сертифікат і для HTTPS, і для Data Protection? {#3}

Код дозволяє обидва варіанти: параметри окремі, той самий відбиток можна передати двічі
(`docs/build/11-install-guide.md` §2.7: «може бути той самий сертифікат … а може й інший»).

| | Той самий сертифікат замовника | Окремий сертифікат Data Protection |
|---|---|---|
| Що потрібно | один PFX | PFX замовника для HTTPS + будь-який RSA-сертифікат із закритим ключем для Data Protection (може бути самопідписаний: браузер його не бачить, довіра не потрібна) |
| Щорічна заміна HTTPS | міняється й сертифікат Data Protection — **старий не можна видаляти зі сховища** (розділ 9.2) | Data Protection не зачіпається |
| Ключ RSA, експортований | обов'язково | лише для сертифіката Data Protection |
| Ризик | видалили старий сертифікат після заміни — сеанси скинуто, **секрети каналів сповіщень не розшифровуються** | менше рухомих частин |

**Рекомендація (судження, не вимога):** HTTPS — сертифікатом замовника з SAN; Data
Protection — окремим довгостроковим RSA-сертифікатом, однаковим на всіх вузлах. Якщо ІБ
замовника вимагає один сертифікат на все — працює, але дотримуйтесь розділу 9.2 при кожній
заміні.

## 4. Покроково: встановлення з HTTPS {#4}

Усе — PowerShell **від імені адміністратора** на кожному сервері застосунку.

### 4.1. Імпорт PFX у сховище машини

```powershell
$pfxPassword = Read-Host -AsSecureString -Prompt 'Пароль PFX'
Import-PfxCertificate -FilePath .\ecr.pfx -CertStoreLocation Cert:\LocalMachine\My `
    -Password $pfxPassword            # додайте -Exportable, якщо сертифікат треба переносити на інші вузли
```

Саме `LocalMachine\My` («Особисті» в `certlm.msc`), **не** `CurrentUser\My`: скрипт і
застосунок шукають лише там.

### 4.2. Перевірка сертифіката

```powershell
$thumb = '<ВІДБИТОК>'                     # Get-ChildItem Cert:\LocalMachine\My | Format-List Subject, Thumbprint, NotAfter
$cert  = Get-Item "Cert:\LocalMachine\My\$thumb"
$cert.HasPrivateKey                        # True
$cert.DnsNameList                          # імена з SAN: має бути ім'я, за яким відкриватимуть ECR
$cert.EnhancedKeyUsageList                 # Server Authentication (1.3.6.1.5.5.7.3.1) або порожньо
$cert.PublicKey.Oid.FriendlyName           # RSA — потрібно, якщо цей сертифікат і для Data Protection
$cert.NotAfter
```

### 4.3. Право на закритий ключ для облікового запису служби

Служба працює під `-ServiceAccount` (рекомендовано gMSA `DOMAIN\ecr-svc$`,
`docs/build/10-installer.md` §1.4). Ні `deploy-ecr.ps1`, ні майстер право **не надають** —
обидва лише нагадують. Без права сертифікат знаходиться (`HasPrivateKey = True`), але
TLS-рукостискання не відбувається (`11-install-guide.md` §2.7, п. 5).

**Через інтерфейс:** `certlm.msc` → Особисті → Сертифікати → сертифікат → «Усі завдання» →
«Керування закритими ключами…» → додати обліковий запис служби → «Читання».

**Скриптом** (для RSA-ключа; працює і для ключа CNG, і для CAPI):

```powershell
$thumb   = '<ВІДБИТОК>'
$account = 'DOMAIN\ecr-svc$'
$cert = Get-Item "Cert:\LocalMachine\My\$thumb"
$rsa  = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if ($rsa -is [System.Security.Cryptography.RSACng]) {
    $keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($rsa.Key.UniqueName)"
} else {
    $keyFile = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($rsa.CspKeyContainerInfo.UniqueKeyContainerName)"
}
$acl = Get-Acl $keyFile
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($account, 'Read', 'Allow')))
Set-Acl -Path $keyFile -AclObject $acl
```

Повторіть для сертифіката Data Protection, якщо він окремий. Без `-ServiceAccount` служба
лише зареєстрована й не стартує (`deploy-ecr.ps1`, крок 6) — тоді право дається тому
обліковому запису, під яким її запустять.

### 4.4. Ім'я хоста

- DNS-запис (A або CNAME) для імені з SAN → сервер (або балансувальник).
- Користувачі відкривають `https://<ім'я з SAN>/` — не IP і не коротке ім'я.
- Вхід Windows (Negotiate) за аліасом: щоб працював Kerberos, а не відкат на NTLM, для
  аліасу потрібен SPN `HTTP/<ім'я з SAN>` на обліковому записі служби
  (`setspn -S HTTP/ecr.customer.local DOMAIN\ecr-svc$`). У проєкті це не перевірялось
  (живого AD немає, `WORK-QUEUE` HU-7) — висновок за загальною поведінкою Windows.

### 4.5. Розгортання

Спершу план без змін у системі:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
$cs = Read-Host -AsSecureString -Prompt 'Рядок підключення'

.\tools\deploy-ecr.ps1 -SqlInstance '<SQL-сервер>' -Database 'ECR' -MsiPath '.\Ecr.msi' `
    -ServiceAccount 'DOMAIN\ecr-svc$' -ConnectionString $cs `
    -DataProtectionThumbprint '<відбиток Data Protection>' `
    -HttpsThumbprint '<відбиток HTTPS>' -AppPort 443 -HttpRedirectPort 80 -WhatIf
```

Крок 1 під `-WhatIf` теж перевіряє сертифікати (лише читання сховища): наявність, закритий
ключ, строк. Далі той самий виклик без `-WhatIf` (на першому розгортанні —
`-BootstrapPassword … -FirstDeployment`, `11-install-guide.md` §2.2).

Що запише скрипт у `Environment` служби `EcrApi` (`Resolve-TransportConfig`):

| Змінна | Значення |
|---|---|
| `ASPNETCORE_URLS` | `https://+:443;http://+:80` (без `-HttpRedirectPort` — лише `https://+:443`) |
| `ECR_Transport__Https__CertificateThumbprint` | відбиток HTTPS |
| `ECR_Transport__Https__Port` | `443` — лише з `-HttpRedirectPort`; без нього змінна видаляється |
| `ECR_Auth__RequireHttps` | `true` — завжди явно |
| `ECR_Auth__DataProtection__CertificateThumbprint` | відбиток Data Protection |

- `-AppPort` — порт HTTPS і правило брандмауера MSI (`APP_PORT`). Типове `5000` дасть
  `https://сервер:5000/`; для `https://сервер/` — `-AppPort 443`.
- ⛔ Транспорт і відбитки потрібні на **кожному** оновленні: оновлення MSI стирає
  `Environment` служби (`operations-runbook.md` §10.3).
- Крок 7 іде по TLS на `127.0.0.1:<AppPort>` із пришпиленням відбитка
  (`Invoke-PinnedHttpsProbe`): доводить, що Kestrel віддає **саме** заданий сертифікат.
  Ім'я в сертифікаті цей зонд не перевіряє.

**Майстер** `EcrSetup.exe`: крок «Transport (HTTPS)» показує сертифікати з
`LocalMachine\My` із закритим ключем, порт береться з кроку «Service Account and Network».
Перенаправлення порту в майстрі **немає** (розділ 12) — для нього `deploy-ecr.ps1`.

## 5. Перенаправлення порту http → https {#5}

`-HttpRedirectPort 80` (лише разом із `-HttpsThumbprint`; дорівнювати `-AppPort` не може):

- Kestrel слухає ще й `http://+:80`; кожен запит туди отримує **308** на HTTPS-порт
  (`UseHttpsRedirection`, `HttpsPort = ECR_Transport__Https__Port`, `HttpsTransport.cs`).
  З `-AppPort 443` адреса в `Location` без порту.
- ⚠ MSI відкриває в брандмауері **лише** `-AppPort`. Правило для 80 — вручну (скрипт
  попереджає). Щоб воно було не ширшим за правило MSI (профіль домену, локальна підмережа,
  `Service.wxs`):

  ```powershell
  New-NetFirewallRule -DisplayName 'ECR redirect' -Direction Inbound -Protocol TCP -LocalPort 80 `
      -Action Allow -Profile Domain -RemoteAddress LocalSubnet
  ```

- Порт 80 (або 443) не має бути зайнятий іншим сервером (IIS, `HTTP.sys`): інакше Kestrel не
  прив'яжеться й служба не стартує. Перевірка: `Get-NetTCPConnection -LocalPort 80,443 -State Listen`.
- HSTS: на запити, що прийшли по HTTPS, застосунок віддає
  `Strict-Transport-Security: max-age=31536000` (без `includeSubDomains`,
  `SecurityHeadersMiddleware`). Після першого відвідування браузер сам іде на HTTPS для цього
  імені — повернутися на HTTP для нього можна лише з боку клієнта.

## 6. Режим `-BehindHttpsProxy` {#6}

Коли TLS завершує проксі/балансувальник (IIS ARR, nginx, F5) із сертифікатом замовника, а
застосунок за ним:

- `ASPNETCORE_URLS=http://+:<AppPort>`, `ECR_Auth__RequireHttps=true` (cookie `Secure`
  працює: браузер говорить із проксі по HTTPS). `-HttpsThumbprint` і `-HttpRedirectPort` не
  задаються; `ECR_Transport__Https__*` скрипт видаляє.
- Сертифікат з SAN ставиться **на проксі**. На сервері застосунку лишається лише сертифікат
  Data Protection (розділ 7) — `-DataProtectionThumbprint` обов'язковий і тут.
- Застосунок не читає `X-Forwarded-*`: HSTS і перенаправлення http→https — на проксі.
  Обмежувач входу бачить IP проксі; `ECR_Security__RateLimit__TrustForwardedFor=true` —
  лише якщо заголовок ставить довірений проксі, а прямого доступу до Kestrel немає
  (скрипт цю змінну не пише — вручну).
- Закрийте порт Kestrel для всіх, крім проксі (скрипт попереджає).
- Вхід Windows (Negotiate) за проксі зазвичай не працює; форма логін/пароль працює.
- Перевірка `transport` про проксі не знає: зелена, бо `RequireHttps = true`
  (`TransportHealthCheck.cs`) — навіть якщо проксі немає.

Докладніше — `11-install-guide.md` §2.7, «Режим за зворотним проксі».

## 7. Data Protection: ключі й сертифікат {#7}

- Кільце ключів — у таблиці `sec.DataProtectionKey` тієї ж бази (`PersistKeysToDbContext`,
  ім'я застосунку `Ecr`), тому всі вузли ділять одне кільце (D-32) і рестарт не скидає
  сеанси. Ключі живуть стандартні 90 днів (окремого налаштування в коді немає), потім
  створюється новий.
- Кожен ключ шифрується сертифікатом із `-DataProtectionThumbprint`
  (`ProtectKeysWithCertificate`). Розшифрувати потрібен **закритий** ключ — звідси право
  читання для облікового запису служби (розділ 4.3) **на кожному вузлі**.
- ⛔ Production без сертифіката — відмова старту (S11). Згоду
  `Auth:DataProtection:AllowUnprotectedKeys` на майданчику не вмикати (лише одноразові
  стенди; `deploy-ecr.ps1` її не ставить ніколи).
- Заданий відбиток, а сертифіката немає / сховище недоступне — відмова старту з назвою
  ключа `Auth:DataProtection:CertificateThumbprint`, не тихий відкат.
- Кілька вузлів: **один і той самий** сертифікат (експорт PFX → імпорт на кожен вузол,
  `Export-PfxCertificate`), інакше вузли не розшифрують ключі один одного.
- Що захищено ключами: cookie сеансу і **секрети каналів сповіщень**. Втрата ключів
  (видалений сертифікат, база без нього) — усіх розлогінено, секрети каналів треба ввести
  наново (`operations-runbook.md` §6.1, §6.4).
- Бекап: PFX сертифіката Data Protection зберігайте окремо від бекапу бази
  (`operations-runbook.md` §6.1).
- Перший старт із сертифікатом на базі, де ключі лежали відкрито, — одноразова ротація
  старих ключів, `operations-runbook.md` §6.4.

## 8. Перевірка після встановлення {#8}

На сервері:

```powershell
# 1. Служба працює
Get-Service EcrApi | Format-List Status, StartType

# 2. Змінні транспорту (фільтр — щоб не вивести рядок підключення)
(Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\EcrApi -Name Environment).Environment |
    Where-Object { $_ -match '^(ASPNETCORE_URLS|ECR_Transport__|ECR_Auth__RequireHttps|ECR_Auth__DataProtection__)' }

# 3. Порти слухаються
Get-NetTCPConnection -LocalPort 443, 80 -State Listen -ErrorAction SilentlyContinue

# 4. Журнал старту: «Транспорт: HTTPS, сертифікат завантажено, діє до …»
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'ECR' } -MaxEvents 20 |
    Format-List TimeCreated, LevelDisplayName, Message
```

З **робочого місця** (не з сервера — `localhost` браузер вважає безпечним, тож проблему з
cookie там не видно):

```powershell
$name = 'ecr.customer.local'                                   # ім'я з SAN
Test-NetConnection $name -Port 443                             # TcpTestSucceeded : True

# Ланцюг і ім'я перевіряє сам Invoke-RestMethod: помилка тут = SAN або довіра
$ready = Invoke-RestMethod "https://$name/health/ready"
$ready.status                                                  # Healthy або Degraded (Degraded — дивись checks)
$ready.checks | Where-Object name -eq 'transport' | Select-Object status, description, data
#   data: requireHttps = True, httpsCertificate = True, certificateNotAfter, certificateDaysLeft

# Перенаправлення (лише з -HttpRedirectPort) і HSTS
curl.exe -sI "http://$name/"                                   # HTTP/1.1 308, Location: https://ecr.customer.local/
curl.exe -sI "https://$name/" | Select-String 'Strict-Transport-Security'
```

У браузері:

1. Відкрити `https://<ім'я з SAN>/` — без попередження про сертифікат.
2. Увійти й перейти на будь-яку іншу сторінку — **без** `401` (симптом HTTP без `Secure`).
3. `/admin/health`: картка «Transport (HTTPS)» зелена; у картці `db` немає обмеження
   «Session keys are stored unencrypted» (ключі Data Protection захищені).

## 9. Заміна сертифіката {#9}

### 9.1. Сертифікат HTTPS

1. Імпортувати новий PFX у `LocalMachine\My` (4.1), перевірити (4.2), дати право на ключ (4.3).
2. Повторити `deploy-ecr.ps1` з **усіма** параметрами оновлення і новим `-HttpsThumbprint` —
   служба перезапуститься, крок 7 перевірить, що віддається новий сертифікат.
   Швидкий шлях без MSI: змінити `ECR_Transport__Https__CertificateThumbprint` в
   `Environment` служби і `Restart-Service EcrApi` (`operations-runbook.md` §11).
3. Старий сертифікат HTTPS можна видалити — **якщо** він не є сертифікатом Data Protection.

### 9.2. Сертифікат Data Protection (і випадок «той самий сертифікат»)

Окремої процедури в `operations-runbook.md` немає (розділ 12); те, що випливає з коду:

1. Новий сертифікат — у `LocalMachine\My` **на кожному вузлі**, право на ключ.
2. `deploy-ecr.ps1 … -DataProtectionThumbprint '<новий>'` (той самий виклик, що й оновлення).
3. ⛔ **Старий сертифікат не видаляти** і не забирати в служби право на його ключ. Нові
   ключі кільця шифруються новим сертифікатом, а вже записані в `sec.DataProtectionKey`
   розшифровуються старим: ASP.NET Core Data Protection шукає сертифікат за відбитком у
   сховищах `My` (поведінка бібліотеки, у цьому лейні на стенді не перевірено). Секрети
   каналів сповіщень, збережені під старим ключем, без нього не розшифруються ніколи.
4. Прибрати старий сертифікат можна лише разом із ротацією ключів: видалити старі ключі
   (за зразком `operations-runbook.md` §6.4) і **перевести секрети каналів сповіщень**
   (`/admin/notifications`), бо вони захищені старими ключами.

Строк дії сертифіката Data Protection застосунок не відстежує (немає ні попередження, ні
жовтої картки) — продовжуйте його разом з HTTPS або видайте окремий довгостроковий
(розділ 3).

## 10. Перевірка на стенді із самопідписаним сертифікатом {#10}

Для DoD `CL-2` («команди перевірені на стенді без реального сертифіката»). **У цьому лейні
не виконано** — потрібен Windows-сервер.

```powershell
# Один RSA-сертифікат на HTTPS і Data Protection, SAN = два імені
$cert = New-SelfSignedCertificate -DnsName 'ecr.test.local', $env:COMPUTERNAME `
    -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 `
    -KeyExportPolicy Exportable -Provider 'Microsoft Software Key Storage Provider' `
    -NotAfter (Get-Date).AddYears(1)
$cert.Thumbprint

# Довіра на стенді (лише тут; на майданчику довіра — від ЦС замовника)
Export-Certificate -Cert $cert -FilePath .\ecr-test.cer | Out-Null
Import-Certificate -FilePath .\ecr-test.cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null

# Ім'я → цей сервер
Add-Content "$env:SystemRoot\System32\drivers\etc\hosts" "127.0.0.1 ecr.test.local"
```

Далі розділи 4.3 → 4.5 (`-HttpsThumbprint $cert.Thumbprint -DataProtectionThumbprint
$cert.Thumbprint -AppPort 443 -HttpRedirectPort 80`) і 8. Що зафіксувати в протоколі: вивід
кроку 1 і 7 `deploy-ecr.ps1`, `transport` з `/health/ready`, `308` з порту 80, вхід із
переходом між сторінками. Додатково — негативні випадки з розділу 11 (`.cer` без ключа,
забране право на ключ, зайнятий порт).

Прибирання: видалити сертифікат з `My` і `Root`, рядок із `hosts`, правило `ECR redirect`.

## 11. Типові збої {#11}

Основна таблиця — `11-install-guide.md` §2.7 «Типові пастки HTTPS» і
`operations-runbook.md` §5. Специфічне для цього сценарію:

| Симптом | Причина | Що робити |
|---|---|---|
| Браузер: «ім'я не збігається» | відкрили за IP, коротким ім'ям або ім'ям вузла, якого немає в SAN | відкривати за іменем із SAN; бракує імені — перевипуск сертифіката |
| Крок 7: служба не відповіла, у журналі помилка захисту каналу | службі немає права читати закритий ключ | розділ 4.3, `Restart-Service EcrApi` |
| Служба не стартує, у журналі «address already in use» / порт зайнятий | 443 або 80 тримає IIS / `HTTP.sys` | звільнити порт або обрати інший `-AppPort`/`-HttpRedirectPort` |
| Сторінка недоступна з частини мереж, на сервері працює | правило брандмауера MSI — лише профіль домену й локальна підмережа | розширити правило (`Set-NetFirewallRule`) за політикою ІБ |
| Після заміни сертифіката всіх розлогінило, канали сповіщень не надсилають | видалили старий сертифікат Data Protection | повернути старий PFX у `LocalMachine\My` з правом на ключ (розділ 9.2) |
| `/health/ready` за проксі зелений, а браузер на HTTP дає `401` | `-BehindHttpsProxy` без проксі або проксі пропускає `http://` | HTTPS на проксі, перенаправлення на проксі |

## 12. Відомі розбіжності {#12}

Розбіжності між скриптами/кодом і тим, що логічно було б описати. Нічого з цього в лейні
`CL-2` не виправлялось (лише документація).

1. **Майстер без перенаправлення порту.** `tools/Ecr.Setup` (`TransportStep`,
   `DeployArguments`) передає лише `-HttpsThumbprint` / `-BehindHttpsProxy` / `-AllowHttp`;
   `-HttpRedirectPort` у майстрі немає. Перенаправлення — лише через `deploy-ecr.ps1`.
2. **Правило брандмауера для порту перенаправлення ширше за правило MSI.** MSI відкриває
   `APP_PORT` лише для профілю домену і `localSubnet` (`Service.wxs`), а команда з
   попередження скрипта (`New-NetFirewallRule … -LocalPort <порт> -Action Allow`) — для
   всіх профілів і будь-якої адреси. У розділі 5 команда з `-Profile Domain -RemoteAddress
   LocalSubnet`.
3. **Правило MSI — лише локальна підмережа.** `Scope="localSubnet"` у `Service.wxs`: робочі
   місця або проксі з інших підмереж до порту не дістануться, хоча
   `11-install-guide.md` §2.7 каже просто «MSI відкриває в брандмауері `APP_PORT`».
4. **Крок 1 не перевіряє EKU і SAN.** `Get-HttpsCertificateProblem` перевіряє формат
   відбитка, наявність, закритий ключ і строк. Сертифікат без Server Authentication проходить
   крок 1 і валить старт служби вже після MSI (видно лише на кроці 7). SAN не перевіряє ніхто:
   зонд кроку 7 приймає будь-яке ім'я і звіряє лише відбиток.
5. **Крок 1 не перевіряє право служби на закритий ключ.** Скрипт і майстер лише нагадують;
   `HasPrivateKey` у скрипті рахується від імені адміністратора, не облікового запису служби.
6. **Сертифікат Data Protection: тип ключа не перевіряється.** Скрипт перевіряє лише
   наявність і `HasPrivateKey`; застосунок (`AuthenticationSetup.FindCertificate`) — лише
   наявність (без `HasPrivateKey`, на відміну від `HttpsTransport.Select`). ECDSA-сертифікат
   пройде крок 1, а шифрування ключів кільця потребує RSA — збій буде вже в застосунку
   (висновок з `EncryptedXml`, на стенді не перевірено).
7. **Немає процедури заміни сертифіката Data Protection.** `operations-runbook.md` §11
   описує заміну лише HTTPS-сертифіката; про те, що старий сертифікат Data Protection не
   можна видаляти, документації не було (розділ 9.2 тут).
8. **Строк сертифіката Data Protection не відстежується** — на відміну від HTTPS
   (`transport`, журнал старту, попередження скрипта за 30 днів).
9. **Зонд кроку 7 — лише TLS 1.2** (`Invoke-PinnedHttpsProbe`, `SslProtocols::Tls12`). На
   сервері, де політика лишає тільки TLS 1.3, крок 7 не дочекається відповіді, хоча служба
   працює (висновок з коду, не перевірено).
10. **`/health/ready` у режимі проксі не знає про проксі** — `transport` зелений за
    `RequireHttps = true`, навіть якщо проксі немає (задокументовано в
    `TransportHealthCheck.cs`, тут — для повноти).
11. **`ECR_Security__RateLimit__TrustForwardedFor`** для режиму проксі `deploy-ecr.ps1` не
    пише й не має параметра; після оновлення MSI (стирає `Environment`) її треба
    виставляти знову вручну.
