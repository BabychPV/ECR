# ECR: перше тестування — від нуля до входу

Цей файл лежить у корені набору `ECR-first-test-<версія>.zip`. Набір самодостатній:
на сервері **не потрібні** клон репозиторію, .NET SDK, `dotnet-ef`, Node. Повні
інструкції — `docs/install-guide.md` і `docs/TESTER-HANDOVER.md`; тут мінімум, щоб
дійти до першого входу.

## Що в наборі

| Файл | Навіщо |
|---|---|
| `Ecr.msi`, `Ecr.msi.sha256` | застосунок (служби `EcrApi`, `EcrWorker`) і еталонний хеш |
| `deploy-ecr.ps1` | один виклик: база → схема → MSI → секрети → старт → `/health` |
| `migration.sql`, `sql\*.sql` | схема бази; лежать **поруч** із `deploy-ecr.ps1`, тому він бере їх звідси і `dotnet ef` не кличе |
| `verify-msi.ps1` | перевірка хешу й підпису MSI до встановлення |
| `docs\` | install-guide, TESTER-HANDOVER, TESTER-GUIDE, сценарії, адмін-доки, реліз-нотатки |
| `BUNDLE-INFO.txt` | версія, коміт, хеш MSI, остання міграція — **вказуйте у кожній заявці** |
| `SHA256SUMS.txt` | суми всіх файлів набору |

## 0. Передумови (один раз)

- Windows Server x64 (2019/2022), PowerShell 5.1, права адміністратора.
- SQL Server 2016 SP1+ **Standard/Enterprise/Developer** (Express — лише з `-AllowExpress`,
  без SQL Agent). Обліковий запис, яким запускаєте скрипт, має право `CREATE DATABASE`.
- `sqlcmd` у `PATH` (`sqlcmd -?` відповідає).
- ~15,5 ГБ вільних на диску даних SQL Server (файлові групи).
- Сертифікат із закритим ключем у `Cert:\LocalMachine\My` — один і для HTTPS, і для
  Data Protection (`D-267`). Без нього служба в Production **не стартує**. Для стенда
  годиться самопідписаний (крок 3).

## 1. Розпакувати й перевірити

```powershell
Expand-Archive .\ECR-first-test-<версія>.zip -DestinationPath C:\ECR-install
cd C:\ECR-install\ECR-first-test-<версія>
Get-ChildItem -Recurse | Unblock-File          # zip із браузера/пошти позначено «з інтернету»
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

# хеш zip — проти ECR-first-test-<версія>.zip.sha256, отриманого разом із набором
.\verify-msi.ps1 -MsiPath .\Ecr.msi -IntegrityOnly   # [I1] має бути PASS
```

`[I2]` (підпис) поки — `WARN`: MSI не підписано, доки не обрано варіант із
`docs/MSI-SIGNING-OPTIONS.md`. Це очікувано.

## 2. Обліковий запис служби

- **Рекомендовано** — gMSA `DOMAIN\ecr-svc$`: `-ServiceAccount 'DOMAIN\ecr-svc$'`; йому
  потрібні доступ до бази й право читання закритого ключа сертифіката
  (`certlm.msc` → сертифікат → «Усі завдання» → «Керування закритими ключами»).
- **Стенд на одній машині** — без `-ServiceAccount`: служби стають під `LocalSystem`,
  реєструються, але **не стартують самі** (крок 4 закінчиться очікуванням `/health/live`
  — це не збій). Тоді в SQL Server має бути логін комп'ютера/`NT AUTHORITY\SYSTEM` з
  доступом до бази.

## 3. Сертифікат для стенда (якщо замовник ще не видав)

```powershell
$cert = New-SelfSignedCertificate -DnsName 'ecr.test.local', $env:COMPUTERNAME `
    -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 `
    -KeyExportPolicy Exportable -Provider 'Microsoft Software Key Storage Provider' `
    -NotAfter (Get-Date).AddYears(1)
$cert.Thumbprint     # запишіть: він піде і в -DataProtectionThumbprint, і в -HttpsThumbprint
```

Довіра в браузері тестувальника й запис у `hosts` — `docs/admin/https-certificate.md` §10.2.
PFX забекапте окремо від бази: без нього збережені сеанси й ключі не розшифрувати.

## 4. Розгортання

Викликати **у цій самій сесії PowerShell** (`.\deploy-ecr.ps1`, не `powershell -File`):
`SecureString` не переходить у інший процес.

```powershell
$cs = Read-Host -AsSecureString -Prompt 'Рядок підключення'   # напр. Server=SQL01;Database=ECR;Integrated Security=true;TrustServerCertificate=True
$bp = Read-Host -AsSecureString -Prompt 'Разовий пароль bootstrap (запишіть)'

# 4.1 План: нічого не змінює
.\deploy-ecr.ps1 -SqlInstance 'SQL01' -Database ECR -MsiPath .\Ecr.msi `
    -ServiceAccount 'DOMAIN\ecr-svc$' `
    -DataProtectionThumbprint '<відбиток>' -HttpsThumbprint '<той самий відбиток>' -AppPort 443 `
    -WhatIf

# 4.2 Перше розгортання на порожній базі
.\deploy-ecr.ps1 -SqlInstance 'SQL01' -Database ECR -CreateDatabaseIfMissing -MsiPath .\Ecr.msi `
    -ServiceAccount 'DOMAIN\ecr-svc$' -ConnectionString $cs -BootstrapPassword $bp `
    -DataProtectionThumbprint '<відбиток>' -HttpsThumbprint '<той самий відбиток>' -AppPort 443 `
    -FirstDeployment
```

У виводі кроку 2 має бути «migration.sql уже в пакеті — dotnet ef не викликається».
Якщо скрипт просить .NET SDK — `sql\` або `migration.sql` не поруч зі скриптом
(розпаковано не повністю).

Варіанти:
- без сертифіката HTTPS, лише стенд: замість `-HttpsThumbprint … -AppPort 443` —
  `-AllowHttp` (порт 5000; `/health/ready` буде `Degraded` через `transport` — так і має бути);
- без `-ServiceAccount` (розділ 2): після скрипта `Start-Service EcrApi, EcrWorker`.

## 5. Перевірка й перший вхід

```powershell
Get-Service EcrApi, EcrWorker                                # обидві Running
Invoke-RestMethod https://ecr.test.local/health/ready        # status: Healthy або Degraded з причиною
```

1. Відкрити `https://<ім'я з сертифіката>/` (або `http://<сервер>:5000/` з `-AllowHttp`)
   з **іншої** машини, ніж сервер.
2. Логін `bootstrap`, пароль — з `-BootstrapPassword`. Система одразу вимагає новий:
   ≥ 12 символів, без імені користувача, не з переліку поширених.
3. `/admin/security`: завести собі адміністратора, автора даних (`DataEntry`) і
   погоджувача (`Approver`), видати ресурсний грант на проєкт. `bootstrap` вимкнеться
   сам, щойно інший користувач отримає `Security.ManageUsers` — заведіть свого
   адміністратора **до** цього.
4. Порожня база не має шаблонів і проєктів: далі сценарії В-1 → В-2 → В-3 → Г-1
   (`docs/TESTER-HANDOVER.md` §3.5, §4).

Застрягли — `docs/TESTER-HANDOVER.md` §9 (FAQ) і `docs/install-guide.md` §7–8
(журнали `%ProgramData%\ECR\logs`, журнал подій `Application`, джерело `ECR`).

## 6. Відомі обмеження першого тестування (не дефекти)

Повний перелік — `docs/TESTER-HANDOVER.md` §5 і `docs/release-notes/`. Найважливіше:

1. **PI на стенді немає**: джерела PI відключені або порожні; сценарії збору з PI
   (И-2…И-5, И-9, И-10) пропускаються (`TESTER-HANDOVER` §4.2).
2. **MSI не підписано** (`[I2]` — WARN), цілісність — лише SHA-256.
3. **SMTP типово не налаштовано**: листи лишаються `Pending`/`Failed` без повтору.
4. **ru/kz — машинний переклад**; `login.title` у ru/kz англійською (свідомо).
5. **Звірки з чинною системою, пілоту RDL і замірів на залізі замовника не було.**
6. **Оновлення стирає `Environment` служб**: після кожного `msiexec` повторіть
   `deploy-ecr.ps1` (без `-BootstrapPassword` і `-FirstDeployment`).
7. **Втрачено пароль `bootstrap`** — відновлення лише новою порожньою базою
   (`TESTER-HANDOVER` §3.7); дані старої не переносяться.
8. Дрібне, відоме: подання аркуша зі застарілою версією визначення дає `404` замість
   `409`; дуже швидкий набір у сітці (інтервал ≈ 0 мс) може губити символи (P3).
9. Після набору цифри в клітинці клавіша ↓/↑ рухає курсор у полі (а не зберігає
   значення і переходить): завершуйте введення Enter або Tab, перш ніж переходити в
   сусідню клітинку; швидкий набір різних клітинок чутливий до паузи менше 70 мс
   (відомий залишок); F2 у сітці документа не працює. Дату в англомовному
   інтерфейсі вводити у форматі yyyy-MM-dd (форма dd.MM.yyyy приймається в ru/kz).

## 7. Дефект

GitHub Issues у `BabychPV/ECR` (шаблон «defect»). У заявці: `version` і `commit` з
`BUNDLE-INFO.txt`, роль, кроки, очікуване/фактичне, `X-Correlation-Id` з відповіді
або журналу. Пріоритети й строки — `docs/TESTER-HANDOVER.md` §6.
