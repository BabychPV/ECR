# Вікно b13 — порядок кроків (живий документ)

Вікно — лише за словом людини. Не 02:15 / 03:30. RESTORE — лише словом людини.
Live: EcrLandUi, API :5094, UI :6094. LIVE-SHA зараз 8e8f2ac04 (b12).
Пачка: lane/integ-b13 @ d75032896 (база 184bc11f6).

## 0. Перед вікном
- [x] Пачка зібрана: 20 picks без конфліктів, контракт без змін; CI 45621188280 success (d75032896).
- [x] e2e: повний 45615038478 на daac96671 — усе зелене, крім registryTableHeader (застаріла передумова) → фікс 88c918bb0; grep-прогін 45621205183 на d75032896: registryTableHeader 5/5. d75032896 відрізняється від daac96671 лише цим spec-файлом. airLimitsReal червоний лише в grep-підмножині (залежність від інших спеків) → картка integ-e2e-airlimits-isolation; у повному прогоні зелений.
- [x] Репетиція на свіжій копії live (G:\EcrData, ім'я EcrLand*), тим самим SHA: idempotent ×2 (2-й = 0 змін), setup -Upgrade, старт API, health, лічильники до/після, deletion-preview для 5 проєктів (read-only).
- [ ] Людині: «що побачите / чого ще не буде».

## 1. Знімок «до» (лише читання)
- [ ] COPY_ONLY бекап EcrLandUi → E:\EcrBackup\land-ui\b13\bak\before-*.bak; лічильники.

## 2. Розгортання
- [ ] Зупинити API лише за PID :5094; нова тека app-b13; міграції idempotent; Sql/*.sql + сід (старт); health 200; LIVE-SHA.

## 3. Кроки даних
- немає (b13 — код). Після: deletion-preview 5 проєктів (read-only, AC-6 project-delete).

## 4. Після
- [ ] Лічильники «після» = «до» (doc 96: 510 результатів; проблеми розрахунку не зросли).
- [ ] UI-прохід: перезавантаження після оновлення (chunk-reload), Мої групи, банер обов'язкових, числа (дозволи 1440, склад газу), видалення проєкту (лише діалог/preview), довідник «as of» (поки без споживача).
- [ ] Нуль слідів; картки → live-verified.


## Репетиція b13 на копії live (25.11, SHA d75032896) — EcrLandB13Reh на G:
Копія: COPY_ONLY бекап EcrLandUi → G:\EcrData\b13reh\EcrLandUi-live-before-b13-reh.bak (6 с) → RESTORE як НОВА EcrLandB13Reh (WITH MOVE у G:\EcrData\b13reh, 14 с). Live і інші бази не чіпали. Вільно: G: 178 ГБ, RAM 11,8 ГБ.
Збірка: detached worktree C:\ecr-wt\b13-reh @d75032896 → publish C:\ecr-wt\b13-bld\app (111 с). Лічильники: C:\ecr-wt\b12-bld\counters.sql → G:\EcrData\b13reh\out\counters-before/after.txt.

| Крок | Команда | Час | Результат |
|---|---|---|---|
| idempotent ×2 | `dotnet ef migrations script --idempotent ...`; `sqlcmd -S localhost -E -d EcrLandB13Reh -I -b -f 65001 -i migrate-idempotent.sql` ×2 | 64 с (генерація) / 3 с / 0 с | rc 0 обидва, 0 рядків виводу (0 змін); міграцій 139, остання 20261124230554_P1124DropConsistencyRule (live уже на 139 після b12). PASS |
| Sql + seed | `powershell -File tools\setup-dev-db.ps1 -Server localhost -Database EcrLandB13Reh -Upgrade` | 141 с | rc 0. PASS |
| старт API | C:\ecr-wt\b13-bld\start-api.ps1 (порт 5198, прапори як restart-w15z; БД лише EcrLand*Reh*) | 15 с | health/ready 200, health/live 200 (PID 14340) |
| акаунт | new-account.ps1 f13-b13reh-sa (SystemAdministrator) лише на копії; пароль у G:\EcrData\b13reh\acc\sysadmin.tmp, не друкувався | — | вхід OK |
| deletion-preview (GET, лише читання; видалення НЕ виконувалось) | GET /api/v1/projects/{id}/deletion-preview | — | див. нижче. PASS |
| лічильники | counters.sql before vs after | — | 0 розбіжностей (migrations=139 обидва, result\|96=510 обидва). PASS |

Preview (повний JSON: G:\EcrData\b13reh\out\deletion-preview.txt):
- LANDUI2026 (id 1): ДОЗВОЛЕНО (blocker null); документи 96 (21 969 клітинок) і 100 (6 927); клітинок 28 896, результатів 564, прогонів 7, проблем 103, подій погодження 186, станів 22, знімків подання 63, періодів 12; погоджені місяці [202611]; залежних 0.
- HSE400_2026 (id 7): ВІДМОВА, blocker `err.ECR-PRD-0409.purgeHasDependants`; залежні: документи 96 і 100 (LANDUI2026) через SourceBinding:HSE400_LAND_COMPOSITION:2026; документ 98, клітинок 1 925, результатів 3 575.
- HSE401_2026 (id 4): дозволено; документ 93, клітинок 292 437, результатів 0, прогонів 1.
- TEST_COORD_2026 (id 5): дозволено; документів 0, клітинок 0, періодів 12.
- HSE400_GOLD (id 6): дозволено, попередження `hse400Tools`; документ 97, клітинок 15, результатів 38.
- doc 96: результатів поточних прогонів = 510 (очікувалось 510). PASS

Зупинено лише мій API за PID 14340. EcrLandB13Reh залишена для чистки координатором; worktree C:\ecr-wt\b13-reh і C:\ecr-wt\b13-bld лишені.
**Підсумок репетиції b13: PASS.**

## ВІКНО b13 на live (25.11, GO людини «так оновлюй»)
- 16:2x COPY_ONLY бекап EcrLandUi → E:\EcrBackup\land-ui\b13\bak\EcrLandUi-before-b13-*.bak (27 с, VERIFYONLY ok, sha256 поруч); лічильники «до».
- 16:31:34 зупинено b12 (лише PID 36252/33936); idempotent ×2: 0 рядків/0 змін (139); setup -Upgrade 56 с rc 0.
- 16:33:11 старт app-b13 detached (WMI): API PID 32856, web 33648, health/ready 200, ProductVersion 1.0.0+d75032896. Простій ~1,5 хв. LIVE-SHA = d75032896.
- Лічильники «після» = «до» (0 різниць; doc 96 = 510, проблеми 78/1/4/20 без змін).
- deletion-preview на live (тимчасовий f13-b13-sa SystemAdministrator, лише GET, потім IsActive=0, файл облікових даних видалено): як на репетиції — LANDUI2026 дозволено, HSE400_2026 відмова (Land 96/100 через SourceBinding), HSE401_2026 / TEST_COORD_2026 / HSE400_GOLD (попередження) дозволено. G: E:\EcrBackup\land-ui\b13\out\deletion-preview-live.txt.
- UI: вхід Windows (WORK_MACHINE\Babyc, без ролей) — «My groups» → /my-groups ✓; решта екранів для цього акаунта закрита. Повний UI-прохід — з акаунтом людини (land-owner).
- Знахідка: акаунт без прав на /documents бачить «Page not found», а не «немає доступу» → b14 (doc-flow-ui-polish).

Набір вікна b13: E:\EcrBackup\land-ui\b13 (launch-live-b13.ps1 → start-live-b13.ps1 detached WMI; stop-live.ps1 -ExpectApp app-b13; new-live-temp-account.ps1); лічильники C:\ecr-wt\b12-bld\counters.sql; збірка API C:\ecr-wt\b13-bld\app, web — npm run build у C:\ecr-wt\b13.

