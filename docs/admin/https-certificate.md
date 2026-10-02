# HTTPS під один сертифікат із SAN

✎ 2026-10-01 (`D-267`, `D14-08`, `R-01`, черга `CL-2`). Відповідь людини (3.5.3,
`QUESTIONS-BUSINESS-2026-10-01`): «замовник надасть один сертифікат в якому буде SAN».
Хто завершує TLS (`Q-5`) не підтверджено; дефолт — **сам застосунок** (Kestrel із цим
сертифікатом, `deploy-ecr.ps1 -HttpsThumbprint`). Режим проксі — розділ 6.

Документ описує те, що **є** в коді й скриптах: `tools/deploy-ecr.ps1`,
`src/Ecr.Api/Startup/HttpsTransport.cs`, `src/Ecr.Api/Auth/AuthenticationSetup.cs`
(Data Protection), майстер `tools/Ecr.Setup` (`TransportStep`), MSI
(`installer/Ecr.Installer/Service.wxs`). Довідник усіх трьох транспортів —
`docs/build/11-install-guide.md` §2.7, експлуатація — `operations-runbook.md` §11. Тут —
покроковий шлях для одного випадку: **один сертифікат замовника з SAN**.

⚠ Команди нижче звірені з кодом; на Windows-стенді з `LocalMachine\My` **не прогнано**.
Розділ 10 — сценарій і статус перевірки (2026-10-01 виконано лише експеримент із заміною
сертифіката Data Protection, розділ 10.1).

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
| `-DataProtectionThumbprint` | `ECR_Auth__DataProtection__CertificateThumbprint` | шифрує ключі кільця Data Protection у `sec.DataProtectionKey` (cookie сеансу, секрети каналів сповіщень, пароль SMTP — purpose `Ecr.Smtp.Password.v1`) | обов'язковий (S11): служба в Production не стартує |

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
| 7 | Строк — щонайменше до наступного планового вікна | HTTPS: менше 30 днів — попередження скрипта і жовтий `transport` на `/health/ready`; прострочений або ще не чинний — `deploy-ecr.ps1` **зупиняється** на кроці 1; у вже встановленої служби прострочений сертифікат старт не зупиняє (Error у журналі, `transport` Degraded). Data Protection: строк не перевіряють ні скрипт, ні застосунок, але майстер `EcrSetup.exe` прострочений сертифікат DP **не приймає** |

⚠ Невідомо (питання до ІБ замовника, у `docs/` відповіді немає): точне ім'я хоста (FQDN
аліасу), ЦС, скільки вузлів, чи ключ експортований. Нейтральний дефолт для прикладів —
`ecr.customer.local`, один вузол.

## 3. Один сертифікат і для HTTPS, і для Data Protection? {#3}

Код дозволяє обидва варіанти: параметри окремі, той самий відбиток можна передати двічі
(`docs/build/11-install-guide.md` §2.7: «може бути той самий сертифікат … а може й інший»).

| | Той самий сертифікат замовника | Окремий сертифікат Data Protection |
|---|---|---|
| Що потрібно | один PFX | PFX замовника для HTTPS + будь-який RSA-сертифікат із закритим ключем для Data Protection (може бути самопідписаний: браузер його не бачить, довіра не потрібна) |
| Щорічна заміна HTTPS | міняється й сертифікат Data Protection: старий лишається в сховищі, а його відбиток передається в `-PreviousDataProtectionCertificateThumbprints`, доки з кільця не видалено ключі, зашифровані ним (розділ 9.2–9.3) | Data Protection не зачіпається |
| Ключ RSA, експортований | обов'язково | лише для сертифіката Data Protection |
| Ризик | видалили старий сертифікат (або не передали його відбиток) після заміни — сеанси скинуто, **секрети каналів сповіщень і пароль SMTP не розшифровуються**; `/health/db` Degraded з відбитком | менше рухомих частин |

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

Крок 1 під `-WhatIf` теж перевіряє сертифікати (лише читання сховища): HTTPS — наявність,
закритий ключ, строк (прострочений або ще не чинний — зупинка, < 30 днів — попередження);
Data Protection — лише наявність і закритий ключ; відбитки
`-PreviousDataProtectionCertificateThumbprints` скрипт не перевіряє (їх перевіряє застосунок на
старті — Warning у журналі, розділ 12). Далі той самий виклик без `-WhatIf` (на першому розгортанні —
`-BootstrapPassword … -FirstDeployment`, `11-install-guide.md` §2.2).

Що запише скрипт у `Environment` служби `EcrApi` (`Resolve-TransportConfig`):

| Змінна | Значення |
|---|---|
| `ASPNETCORE_URLS` | `https://+:443;http://+:80` (без `-HttpRedirectPort` — лише `https://+:443`) |
| `ECR_Transport__Https__CertificateThumbprint` | відбиток HTTPS |
| `ECR_Transport__Https__Port` | `443` — лише з `-HttpRedirectPort`; без нього змінна видаляється |
| `ECR_Auth__RequireHttps` | `true` — завжди явно |
| `ECR_Auth__DataProtection__CertificateThumbprint` | відбиток Data Protection |
| `ECR_Auth__DataProtection__PreviousCertificateThumbprints` | відбитки попередніх сертифікатів DP через `;` — лише з `-PreviousDataProtectionCertificateThumbprints`; без параметра скрипт її не пише (але й не видаляє) |

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
  створюється новий. Старі ключі з таблиці **не зникають** (видалення в коді немає) і далі
  потрібні, щоб розшифрувати те, що ними захищено.
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
- Що захищено ключами: cookie сеансу `ecr.auth`, **секрети каналів сповіщень** і **пароль
  SMTP, заданий в адмінці** (`/admin/notifications`, панель SMTP). Не захищено (не залежить
  від сертифіката): облікові дані джерел PI/SQL і пароль SMTP запасного шляху
  (`ECR_Smtp__SecretName`) — це змінні оточення служби `Secrets:*`. Втрата ключів (видалений
  сертифікат, база відновлена без нього) — усіх розлогінено, секрети каналів і пароль SMTP
  треба ввести наново (`operations-runbook.md` §6.1, §6.4).
- Що бачить користувач після втрати ключів: cookie не розшифровується, запит іде анонімним →
  `401` → сторінка входу, без пояснення причини. Адміністратор бачить
  `XmlKeyManager … CryptographicException: Unable to retrieve the decryption key` у журналі й
  `Degraded` на `/health/db` (`unreadableKeyCertificates`). Сеанси й так живуть не довше 12 год
  (абсолютна межа), тож для них втрата разова; для секретів каналів і пароля SMTP — до
  повторного введення.
- Бекап закритого ключа — експорт PFX **одразу після імпорту** (якщо ключ експортований):

  ```powershell
  $p = Read-Host -AsSecureString -Prompt 'Пароль PFX'
  Export-PfxCertificate -Cert 'Cert:\LocalMachine\My\<відбиток>' -FilePath '<сховище секретів>\ecr-dp-<відбиток>.pfx' -Password $p
  ```

  Імпорт без `-Exportable` не дає експортувати ключ — тоді єдина копія — вихідний PFX від ЦС
  замовника; збережіть саме його. PFX зберігайте **окремо** від бекапу бази (бекап бази + PFX
  = можливість підробити сеанс і прочитати секрети). Бекапіть **і попередні** сертифікати DP,
  доки існують бекапи бази з ключами, зашифрованими ними. Втрата PFX = безповоротна втрата
  сеансів, секретів каналів і пароля SMTP (`operations-runbook.md` §6.1).
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
   ⚠ Якщо цей сертифікат — і сертифікат Data Protection (один на все), швидкий шлях змінює
   **три** змінні: `ECR_Transport__Https__CertificateThumbprint`,
   `ECR_Auth__DataProtection__CertificateThumbprint` (новий) і
   `ECR_Auth__DataProtection__PreviousCertificateThumbprints` (старий), а старий сертифікат
   лишається в сховищі. Зміна лише першої лишає DP на старому сертифікаті.
3. Старий сертифікат HTTPS можна видалити — **якщо** він не є сертифікатом Data Protection.

### 9.2. Сертифікат Data Protection (і випадок «той самий сертифікат»)

Що треба знати перед заміною (`AuthenticationSetup.cs`, `DatabaseHealthCheck.cs`):

- Новий відбиток **не перешифровує** вже записані ключі: вони лишаються під старим
  сертифікатом A. Ключ під A лишається ключем за замовчуванням до свого спливу (≈ 90 днів від
  створення), тож ним шифруються й нові дані — зокрема секрети каналів і пароль SMTP, введені
  вже після заміни (поведінка бібліотеки, розділ 10.1).
- Строк ключа (90 днів) означає лише, що після нього нові дані шифруватимуться новим ключем.
  Старий ключ із таблиці не зникає, і все, що ним зашифровано, читається лише з A —
  **безстроково**. Тому A потрібен, **доки з `sec.DataProtectionKey` не видалено ключі,
  зашифровані ним** (розділ 9.3), а не «90 днів».
- ⛔ Щоб A читався, потрібні **обидві** умови: A лишається в `LocalMachine\My` на кожному вузлі
  (з правом служби на закритий ключ) **і** його відбиток передається в
  `-PreviousDataProtectionCertificateThumbprints` (змінна
  `ECR_Auth__DataProtection__PreviousCertificateThumbprints`, D-267, коміт `ed0b2393`). Одне
  без іншого не працює: відбиток у Previous без сертифіката в сховищі дає лише Warning у
  журналі; сертифікат у сховищі без відбитка в Previous дає `Degraded` на `/health/db` з
  відбитком у `unreadableKeyCertificates`.
- Видалили A, а PFX ніде немає — дані під ключами A втрачено безповоротно (розділ 11).

Строк дії сертифіката Data Protection застосунок не відстежує (немає ні попередження, ні
жовтої картки): прострочений сертифікат далі шифрує й розшифровує ключі (висновок за
бібліотекою). Але майстер `EcrSetup.exe` прострочений сертифікат DP не приймає і параметра
Previous не має (розділ 12), тож після спливу заміну робіть лише `deploy-ecr.ps1` за 9.3.
Продовжуйте сертифікат DP разом з HTTPS або видайте окремий довгостроковий (розділ 3).

### 9.3. Заміна сертифіката Data Protection — повна процедура

⚠ Процедуру й SQL на стенді не проганяли (SQL складено за форматом XML ключа бібліотеки).
Перевірте на копії бази до першої заміни на майданчику.

1. Новий сертифікат B (RSA, закритий ключ, бажано `Exportable`) — у `LocalMachine\My` на
   **кожному** вузлі, право служби на ключ (4.3). Старий A **не чіпати**.
2. Бекап PFX обох сертифікатів (розділ 7) і повний бекап бази (`operations-runbook.md` §6.2).
3. `deploy-ecr.ps1 … -DataProtectionThumbprint '<B>' -PreviousDataProtectionCertificateThumbprints '<A>'`
   (решта параметрів — як на оновленні). Майстер параметра Previous не має — заміну DP робити
   лише скриптом.
4. Перевірка: журнал старту без Warning «…PreviousCertificateThumbprints: сертифіката(ів) …
   немає»; `/health/db` — не Degraded, `unreadableKeyCertificates` порожній; вхід працює без
   повторного входу.
5. Доки A потрібен — передавати `-PreviousDataProtectionCertificateThumbprints '<A>'` на
   **кожному** розгортанні (оновлення MSI стирає `Environment` служби).
6. Як прибрати A (одне з двох):
   - **без простою для користувачів:** дочекатися, поки ключ під A спливе і з'явиться ключ під
     B (`expirationDate` у запиті нижче), плюс 12 год (абсолютна межа сеансу). Ввести наново
     секрети каналів і пароль SMTP (тепер вони під ключем B). Далі — «видалення»;
   - **одразу:** відразу після кроку 4 виконати «видалення» — усіх розлогінить, секрети
     каналів і пароль SMTP ввести наново.

   **Видалення:** зупинити `EcrApi` на **всіх** вузлах; знайти ключі під A і видалити їх.
   (Запит `operations-runbook.md` §6.4 для цього не годиться: він знаходить лише **відкриті**
   ключі, а ключі під A теж містять `encryptedSecret`.)

   ```powershell
   [Convert]::ToBase64String((Get-Item 'Cert:\LocalMachine\My\<A>').RawData)
   ```

   ```sql
   -- огляд: строк кожного ключа
   SELECT Id, FriendlyName,
     CAST(Xml AS xml).value('(/key/creationDate)[1]','nvarchar(40)')   AS Created,
     CAST(Xml AS xml).value('(/key/expirationDate)[1]','nvarchar(40)') AS Expires
   FROM sec.DataProtectionKey;
   -- ключі під A (значення — base64 з PowerShell вище)
   DELETE FROM sec.DataProtectionKey
   WHERE CAST(Xml AS xml).value('declare namespace ds="http://www.w3.org/2000/09/xmldsig#"; (//ds:X509Certificate)[1]','nvarchar(max)') = N'<base64 сертифіката A>';
   ```

   Запустити службу; наступні розгортання — **без** `-PreviousDataProtectionCertificateThumbprints`
   (Previous прибирається тим, що його перестають передавати; якщо MSI не перевстановлювали —
   видалити змінну з `Environment` вручну). Ввести наново секрети каналів і пароль SMTP, якщо
   ще не введено. `/health/db` — не Degraded.
7. Лише після цього A можна видалити зі сховища. **PFX A не знищувати**, доки зберігаються
   бекапи бази, зроблені до кроку 6: відновлення такого бекапу потребує A
   (`operations-runbook.md` §6.3).

## 10. Перевірка на стенді із самопідписаним сертифікатом {#10}

Для DoD `CL-2` («команди перевірені на стенді без реального сертифіката»).

**Статус: перевірено на стенді 2026-10-01 — частково.**

| Частина | Статус |
|---|---|
| Заміна сертифіката Data Protection (D-267, ризик «старий сертифікат») | **Виконано** на бібліотеці Data Protection (.NET 10, ті самі `ProtectKeysWithCertificate` + `EncryptedXml`, сховище ключів — файлова система замість `sec.DataProtectionKey`; сховище не впливає на шифрування ключа). Результат — розділ 10.1. Саму `Ecr.Api` з заміною не запускали |
| Запуск `Ecr.Api` із сертифікатом (Kestrel, DP, `deploy-ecr.ps1`, кроки 1/7, `transport`, 308, вхід) | **Не виконано: потрібен `LocalMachine\My`.** Код шукає сертифікат лише там (`AuthenticationSetup.FindCertificate`, `HttpsTransport.FindInLocalMachine`; `CurrentUser` і `.pfx` із файла не підтримуються), а на стенді сховище машини не змінювали (обмеження безпеки) |
| Імпорт у `LocalMachine\Root`, рядок у `hosts`, право на закритий ключ (4.3), порти 443/80, брандмауер, негативні випадки (`.cer` без ключа, зайнятий порт) | **Не виконано: потрібен `LocalMachine` / права адміністратора / служба** |

Тестові сертифікати (самопідписані RSA 3072, строк 20 год) створено в пам'яті й у `.pfx` у
тимчасовому каталозі, тимчасово додавались у `Cert:\CurrentUser\My`; після прогону видалено.

### 10.1. Результат: що буде з уже захищеними ключами Data Protection при заміні сертифіката

Ключі кільця зашифровані сертифікатом A. Служба перезапускається з відбитком сертифіката B
(`ProtectKeysWithCertificate(B)`), як робить `AuthenticationSetup`:

| Сценарій | Старт | Старі дані (раніше захищені: cookie, секрети каналів) | Нові дані |
|---|---|---|---|
| A лишився в сховищі `My` (будь-який: `CurrentUser` або `LocalMachine`), служба — на B | без помилок | **розшифровуються** (бібліотека знаходить A за відбитком, що записаний у зашифрованому ключі) | працюють; **старий ключ кільця лишається зашифрований A** і далі діє, поки не спливе його строк (90 днів) — новий ключ B-захищений з'являється лише при його спливі |
| A **видалено**, служба — на B | **не падає** (немає виняткової ситуації старту) — у журналі багато `fail … XmlKeyManager[24] … CryptographicException: Unable to retrieve the decryption key` і `warn … DefaultKeyResolver[12]: Key … is ineligible to be the default key` | **не розшифровуються безповоротно** (`CryptographicException`): усі сеанси/cookie недійсні, секрети каналів сповіщень втрачені | перший запит після старту може теж завершитись помилкою; далі бібліотека видає **новий** ключ (захищений B) — запис у кільці з'являється, нові дані працюють |
| A видалено, служба — на B, але налаштовано `UnprotectKeysWithAnyCertificate(A)` | без помилок | розшифровуються (A береться з налаштування, у сховищі не потрібен) | працюють |

Висновки:

1. Ризик D-267 **підтверджено**: видалення старого сертифіката зі сховища одразу робить
   записані ключі нечитабельними; старт не зупиняється, S11 цього не ловить (перевіряється
   лише наявність сертифіката з налаштованим відбитком). ✎ Після `ed0b2393`: `/health/db`
   (перевірка `db`) стає `Degraded` з відбитками в `unreadableKeyCertificates`; крок 7
   `deploy-ecr.ps1` показує це рядком про перевірку, що не Healthy, але розгортання **не**
   зупиняє (`Degraded` = готово).
2. Заміна відбитка **не перешифровує** вже записані ключі: вони лишаються під A до спливу
   строку (90 днів), і навіть після спливу ключ із таблиці не зникає. Отже, **A потрібен у
   сховищі, доки з кільця не видалено ключі під ним** (розділ 9.3), а не «90 днів» і не «до
   першого перезапуску».
3. `UnprotectKeysWithAnyCertificate` усуває залежність від сховища. **Реалізовано, коміт `ed0b2393`:**
   `Auth:DataProtection:PreviousCertificateThumbprints` (масив або список через `;`),
   `deploy-ecr.ps1 -PreviousDataProtectionCertificateThumbprints`; перевірено тестами на бібліотеці
   (A -> B + Previous=[A] читає, без Previous — `CryptographicException`). Сертифікат із Previous усе одно має бути
   в `LocalMachine\My` (код шукає лише там; ненайдений — Warning у журналі). `/health/db` (перевірка `db`) став
   Degraded, коли в кільці є ключі, зашифровані сертифікатом, якого служба не може використати, — у тексті названо відбитки.
4. Не перевірено: поведінка самої `Ecr.Api` (EF-сховище, `ValidateOnStart`, Windows-служба,
   `LocalMachine`) — лише бібліотека Data Protection.

### 10.2. Сценарій для стенда з правами адміністратора (не виконано)

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
| Після заміни сертифіката всіх розлогінило, канали сповіщень і пошта не надсилають | видалили старий сертифікат Data Protection або не передали його відбиток | повернути старий PFX у `LocalMachine\My` на кожному вузлі з правом на ключ, повторити `deploy-ecr.ps1` з `-PreviousDataProtectionCertificateThumbprints '<старий>'`, перевірити `/health/db` (`unreadableKeyCertificates` порожній). PFX немає ніде — дані під старими ключами втрачено безповоротно: видалити нечитабельні ключі (розділ 9.3, «Видалення»), ввести наново секрети каналів і пароль SMTP; користувачі входять заново |
| `/health/db` Degraded: «key ring holds keys encrypted with certificate(s) that are not available» | відбиток із повідомлення не в Previous або сертифіката немає в сховищі | див. рядок вище |
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
11. **Виправлено кодом, коміт `ed0b2393` (D-267).** Було: `UnprotectKeysWithAnyCertificate` не налаштовано, і при заміні сертифіката видалення
    старого тихо робить кільце нечитабельним (розділ 10.1). Рішення (зміна коду, питання
    власнику зони безпеки): список «старих» відбитків у конфігурації
    (`Auth:DataProtection:PreviousCertificateThumbprints`) + `UnprotectKeysWithAnyCertificate`.
12. **`ECR_Security__RateLimit__TrustForwardedFor`** для режиму проксі `deploy-ecr.ps1` не
    пише й не має параметра; після оновлення MSI (стирає `Environment`) її треба
    виставляти знову вручну.
