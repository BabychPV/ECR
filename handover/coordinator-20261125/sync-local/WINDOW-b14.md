# Вікно b14 — порядок кроків (живий документ)

Вікно — лише за словом людини. Не 02:15 / 03:30. RESTORE — лише словом людини.
Live: EcrLandUi, API :5094, UI :6094. LIVE-SHA зараз d75032896 (b13).
Пачка: lane/integ-b14 @ 7afb19c97 (база d75032896). Цільова тека LIVE: F:\ecr-backup\land-ui\app-b14 (ще НЕ створена).

## 0. Перед вікном
- [x] Пачка зібрана: 12 picks (-x) без конфліктів, контракт без змін; CI 45671209757 success, e2e-stand 45671375248 success (7afb19c97); Evidence зелений (повторний прогін).
- [x] Репетиція на свіжій копії live (G:\EcrData, EcrLandB14Reh), тим самим SHA: idempotent ×2, setup -Upgrade, старт API, Start-LiveDetached (перевірка серверного журналу), health, тест джерела FLERT, лічильники до/після, deletion-preview.
- [ ] Людині: «що побачите / чого ще не буде».

## 1. Знімок «до» (лише читання)
- [ ] COPY_ONLY бекап EcrLandUi → E:\EcrBackup\land-ui\b14\bak\before-*.bak; лічильники (C:\ecr-wt\b12-bld\counters.sql).

## 2. Розгортання
- [ ] Зупинити API лише за PID :5094; нова тека app-b14 (api + web з C:\ecr-wt\b14\src\Ecr.Web\dist); міграції idempotent ×2; setup-dev-db -Upgrade (Sql/*.sql + сід; b14 додає 2 файли сіду: COLL-doc-notices-compact, SRCTEST-flert-test-connection); старт ЧЕРЕЗ `tools\live\Start-LiveDetached.ps1` (-StartScript start-live-b14.ps1 -ExitFile ... -ApiPort 5094) — доказ запису серверного журналу; health 200; LIVE-SHA.

## 3. Кроки даних
- немає (b14 — код). Після: тест джерела FLERT (POST data-sources/1/test → ok:true, integration.test.connectedNoCatalog), deletion-preview 5 проєктів (read-only).

## 4. Після
- [ ] Лічильники «після» = «до» (doc 96: результатів без змін; проблеми розрахунку не зросли).
- [ ] UI-прохід: дошка документів за роком, компактні сповіщення документа, числа (єдиний формат), тест підключення джерела.
- [ ] Нуль слідів; картки → live-verified.

## Репетиція b14 на копії live (25.11, SHA 7afb19c97) — EcrLandB14Reh на G:
Копія: COPY_ONLY бекап EcrLandUi → G:\EcrData\b14reh\EcrLandUi-live-before-b14-reh.bak (4 с) → RESTORE як НОВА EcrLandB14Reh (WITH MOVE у G:\EcrData\b14reh, ~16 с). Live і інші бази не чіпали. Вільно: G: 138 ГБ, RAM 13,4 ГБ.
Збірка: dotnet publish src\Ecr.Api -c Release → C:\ecr-wt\b14-bld\app (121 с), ProductVersion 1.0.0+7afb19c97c2df5e1a79f26ead0fa28ac5b553c03; web dist — npm run build у C:\ecr-wt\b14\src\Ecr.Web (OK). Лічильники: G:\EcrData\b14reh\out\counters-before/after.txt.

| Крок | Команда | Час | Результат |
|---|---|---|---|
| idempotent ×2 | `dotnet ef migrations script --idempotent --project src/Ecr.Infrastructure --startup-project src/Ecr.Infrastructure --output C:\ecr-wt\b14-bld\migrate-idempotent.sql`; `sqlcmd -S localhost -E -d EcrLandB14Reh -I -b -f 65001 -i ...` ×2 | 41 с (генерація) / 5 с / 1 с | rc 0 обидва, 0 рядків виводу (0 змін); міграцій 139, остання 20261124230554_P1124DropConsistencyRule. PASS |
| Sql + seed | `powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrLandB14Reh -Upgrade` (з C:\ecr-wt\b14) | 85 с | rc 0. PASS |
| старт API | C:\ecr-wt\b14-bld\start-api.ps1 (порт 5199, вільний; прапори як start-live-b13; БД лише EcrLand*Reh*) | 16 с | health/live 200, health/ready 200, PID 6044 (власник порту 5199 = 6044) |
| серверний журнал | `powershell -File tools\live\Start-LiveDetached.ps1 -ProcessId 6044` (режим check) | <1 с | `OK: API pid 6044 writes its server log: C:\ProgramData\ECR\logs\ecr-20261125_016.log`, маркер «(pid=6044).», rc 0. PASS |
| акаунт | new-account.ps1 f13-b14reh-sa (SystemAdministrator) лише на копії; пароль у G:\EcrData\b14reh\acc\sysadmin.tmp, не друкувався | — | вхід OK (POST /api/v1/login/local) |
| тест джерела FLERT | POST /api/v1/data-sources/1/test {"reason":...} | — | `{"ok":true,"error":null,"entities":0,"messageKey":"integration.test.connectedNoCatalog"}`. PASS |
| Documents | GET /api/v1/documents?year=2026, /api/v1/documents | — | 200, 200 (окремого ендпоінта board немає: /documents/board → 404, очікувано; дошка — клієнтська над списком) |
| deletion-preview (GET, лише читання; видалення НЕ виконувалось) | GET /api/v1/projects/{id}/deletion-preview ×5 | — | як у b13: LANDUI2026 (1) дозволено; HSE401_2026 (4) дозволено; TEST_COORD_2026 (5) дозволено; HSE400_GOLD (6) дозволено з попередженням hse400Tools; HSE400_2026 (7) ВІДМОВА err.ECR-PRD-0409.purgeHasDependants. PASS. Повний вивід: G:\EcrData\b14reh\out\window-checks.txt |
| лічильники | counters.sql before vs after | — | 0 розбіжностей (migrations=139 обидва; doc 96 результатів = 512 в обох — на live з часу b13-репетиції (510) додалось 2, до/після рівні). PASS |

Зупинено лише мій API за PID 6044. EcrLandB14Reh залишена для чистки координатором; C:\ecr-wt\b14 і C:\ecr-wt\b14-bld лишені. app-b14 на F: НЕ створювалась.
**Підсумок репетиції b14: PASS.**

Набір вікна b14 (потрібно створити перед вікном, за зразком b13): E:\EcrBackup\land-ui\b14 (launch/start-live-b14.ps1, stop-live.ps1 -ExpectApp app-b14, new-live-temp-account.ps1); лічильники C:\ecr-wt\b12-bld\counters.sql; збірка API C:\ecr-wt\b14-bld\app, web — C:\ecr-wt\b14\src\Ecr.Web\dist.

## ВІКНО b14 на live (25.11, GO людини «так на b14»)
- 22:51 COPY_ONLY бекап EcrLandUi → E:\EcrBackup\land-ui\b14\bak\EcrLandUi-before-b14-20261125-2251.bak (28 с, CHECKSUM, VERIFYONLY ok, sha256 поруч); лічильники «до» (out\counters-before.txt). Від b13-після на live змінилось руками людини (перевідкриття/перерахунок doc 96): result|96 510→512, StepError 20→45, MissingInputs 78→74 — не вікно.
- 22:53:37 зупинено b13 лише за PID 32856/33648 (stop-live.ps1 -ExpectApp app-b13); idempotent ×2: rc 0, 0 рядків (139 міграцій).
- setup-dev-db -Upgrade: перша спроба rc 1 за 1 с — у PATH сесії не було dotnet (dotnet ef); додано %LOCALAPPDATA%\Microsoft\dotnet → rc 0, 76 с. Урок: набір вікна має сам ставити PATH dotnet (на репетиції він був у сесії агента).
- 22:55:17 старт app-b14 detached (launch-live-b14.ps1 -ExpectHash 7afb19c97): API PID 22168, web 10620, health/ready 200, web 200, ProductVersion 1.0.0+7afb19c97. Простій ~2 хв. LIVE-SHA = 7afb19c97.
- Start-LiveDetached.ps1 -ProcessId 22168: «OK: API pid 22168 writes its server log … ecr-20261125_016.log (pid=22168)».
- Лічильники «після» = «до»: 0 різниць (out\counters-after.txt).
- Тимчасовий f13-b14-sa (SystemAdministrator; потім IsActive=0, файл облікових даних видалено): FLERT test → 200 {"ok":true,"entities":0,"messageKey":"integration.test.connectedNoCatalog"}; deletion-preview 1/4/5/6/7 → 200, лічильники проєктів як на репетиції (вивід обрізано 300 символами, поле blocker для 7 не потрапило у файл; на репетиції й на b13-live — purgeHasDependants); documents?year=2026 200. out\window-checks-live.txt.
- UI-прохід: чекає входу людини (land-owner) у браузерній панелі — пароль людини не вводжу.
- 23:1x UI-прохід (акаунт людини land-owner, 1440×900): Board = лічильники 3/2, без «No state» ✓; doc 96 «Sheet: Partly submitted · Year 2026», одна смуга проблем 32 px + Details-матриця, перший рядок сітки видно ✓ (чипа Reopened немає — перевідкриття Expired 21:00); Emission limits KZ06VCZ14825472 — одне групування, без «E» ✓; Sources → FLERT → модаль Test connection (не надсилав від імені людини; API ok). Картки b14 → live-verified. Знахідка: #ARG Stationary Equipment січ–сер → integ-land-stationary-arg (planned).
**Вікно b14: PASS.**
