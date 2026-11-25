# Вікно b12 — порядок кроків (живий документ; доповнювати при прийманні кожної lane)

GO людини 24.11 ~23:05: «як завершиш всі зміни починай оновлювати стенд». Не в 02:15 / 03:30. RESTORE — лише при збої за правилом.
Live: EcrLandUi, API :5094 (F:\ecr-backup\land-ui\app-w15z → нова тека app-b12), UI :6094. Вхід у UI — у панелі браузера (людина вже увійшла).

## 0. Перед вікном (на копії, тим самим SHA пачки)
- [ ] lane/integ-b12 зібрано: усі lane accepted; build; Architecture; контракт регенеровано; пуш → ОДИН повний CI + e2e зелені.
- [ ] upgrade main→SHA ×2 + idempotent (2-й прогін 0 змін) на копії live (G:\EcrData).
- [ ] Репетиція ВСІХ кроків нижче на свіжій копії live (G:), тим самим SHA; лічильники до/після.
- [ ] Людині (у звіті): «що побачите / чого ще не буде».

## 1. Знімок «до» (лише читання)
- [ ] COPY_ONLY бекап EcrLandUi → E:\EcrBackup\land-ui\b12\bak\before-*.bak
- [ ] Лічильники: calc.CalculationIssue за видами × документ × період; результати; MethodologyVersion за Engine; rpt-види; MethodologySubstance (реєстри).

## 2. Розгортання
- [ ] Зупинити API (лише за PID процесу :5094), розгорнути збірку b12 у нову теку, build.done з HEAD.
- [ ] Міграції (idempotent): 20261124152432 calc-orphans → 20261124194915 rm-report-snapshots → 20261124230554 consistency (+ gas — без міграції).
- [ ] Sql/*.sql (05-rpt-views — +7 видів SSRS), сід (старт застосунку). health/live, health/ready 200.
- [ ] LIVE-SHA = SHA пачки.

## 3. Кроки даних (кожен: -DryRun → реально → перевірка)
1. [ ] P-1124-2 WaterV2 → FinalizeProve → Finalize (tools/Ecr.MethodologyScriptMigration/window, -ExtraCases finalize-cases). Очікування: формульних версій 78 → 0, 0 обірваних.
2. [ ] P-1124-2 Land 1.0.21.0: Invoke-LandWater1021.ps1 -LiveConfirmed (builder → verifytpl → dryrun → apply → recalc → compare). Очікування: вода 4/4 клітинки 0 різниць, входи без втрат.
3. [ ] c11-10: Invoke-MsRowNarrowing.ps1 (-DryRun → реально). Очікування: C11_10 V5, 28 правил неактивні.
4. [ ] gas-composition: tools/gas-composition/Import-GasCompositionFromFlert.ps1 (dry-run → -Apply → -VerifyOnly). Очікування: 523 / 982 / 22 725, контрольні суми рівні.
5. [ ] Перерахунок doc 96 (202609–202611) — лише непогоджені місяці.
6. [ ] Consistency: «Run check now» → R1/R3 після кроків 1–5 мають зникнути; R8 — info.

## 4. Після
- [ ] Лічильники «після» vs «до»: проблеми розрахунку не зросли (крім очікуваних −6 CategoryRuleRejected, −323 сиріт).
- [ ] UI-прохід зі знімками: методології (лише Script, редактор, повний екран), майстер документа, налаштування проєкту, аудит (час, фільтри), без Report snapshots, склад газів, Consistency issues, doc 96 підказки колонок місяця.
- [ ] Нуль слідів перевірки (тестові акаунти/рядки).
- [ ] Картки → live-verified; ранковий звіт людині.

## Відомі прийняті відхилення
- 2× RowInactive на S3_9 (вода) — наявне з V1; картка integ-script-row-key.
- Склад газів поки не читає жодна методологія (cfg.RegistryUse = 0) — питання людині вранці.

## Репетиція b12 на копії live (25.11, 04:44–05:08, SHA 8e8f2ac04) — EcrLandB12Reh на G:
Копія: COPY_ONLY бекап live → G:\EcrData\b12reh\EcrLandUi-live-before-b12-20261125-0444.bak (6 с) → restore EcrLandB12Reh (82 с).
Ім'я EcrLand* (а не EcrB12Reh): цього вимагає сторож EcrApi.ps1 у збирачі Land.
Збірка: detached worktree C:\ecr-wt\b12-reh @8e8f2ac04 → publish C:\ecr-wt\b12-bld\app (API) і \tool (інструмент MS) (~2,5 хв).
Лічильники: C:\ecr-wt\b12-bld\counters.sql → G:\EcrData\b12reh\out\counters-*.txt. Журнал кроків з точними командами: G:\EcrData\b12reh\out\steps.log.

| Крок | Команда (на live: -Database EcrLandUi, BaseUrl :5094, файли облікових записів вікна) | Час | Результат |
|---|---|---|---|
| idempotent ×2 | `dotnet ef migrations script --idempotent --project src/Ecr.Infrastructure --startup-project src/Ecr.Infrastructure -o migrate-idempotent.sql`; `sqlcmd -S localhost -E -d <db> -I -b -f 65001 -i migrate-idempotent.sql` ×2 | 5 с / 3 с | 136→139 міграцій; 2-й прогін — 0 рядків виводу (0 змін). PASS |
| Sql + seed | `powershell -File tools\setup-dev-db.ps1 -Server localhost -Database <db> -Upgrade` (знову idempotent, Sql/*.sql у порядку $scripts, старт застосунку = seed) | 128 с | PASS; сироти 323→0 (міграція calc-orphans); rpt +7 фіксованих (v_CalculationIssue/Result, v_Document, v_MethodologyOutput/Version, v_Period, v_Project), −1 v_WaterReport_v1 (P-1124-3, за рішенням) |
| старт API | env як у restart-w15z (MonthColumnRouting, SupersededPurge, FromMonthEnabled) | 17 с | health/ready 200, health/live 200 |
| WaterV2 | `window\Invoke-MsMigrationPhase.ps1 -Stage WaterV2 -Work <w> -Database <db> -BaseUrl <api> -CredentialFile <author> -ReviewerCredentialFile <reviewer> -ToolDll <tool>` | 10 с | V2 (id 126) на Script через API. PASS |
| FinalizeProve | те саме, `-Stage FinalizeProve` (сухий прогін Finalize) | 81 с | 78 доведено, 0 заблоковано. PASS |
| Finalize | те саме, `-Stage Finalize` | 13 с | формульних версій 78→0, обірваних 0; Script 125. PASS |
| Land 1.0.21.0 | `window\Invoke-LandWater1021.ps1 -Step <s> -Database <db> -Port <p> -Out <o> -AccountsFile <f>` для snapshot before → builder → verifytpl → dryrun → (BACKUP) → apply -Bak → recalc → snapshot after → compare (на live `-LiveConfirmed`, без `-Step account`) | ~8 хв (збирач 195 с) | шаблон 12/12, dryrun lost 0 / transferred 21 984; вода 4/4, 0 різниць*; входи 254→254; проблеми не зросли; висячих 0. PASS |
| c11-10 | `window\Invoke-MsRowNarrowing.ps1 -Database <db> -Work <w> -BaseUrl <api> -CredentialFile <author> -ReviewerCredentialFile <reviewer> [-DryRun]` | 1 с + 7 с | C11_10 V5 (id 127), прибрано 28 умов. Речовини 555→570 (+15 V5), усі LAND_AIR_SUBSTANCE. PASS |
| газ | `tools\gas-composition\Import-GasCompositionFromFlert.ps1 -EcrServer localhost -EcrDatabase <db> -FlertServer <flert>` → `… -Apply -BaseUrl <api> -Credential <SystemAdministrator>` → `… -VerifyOnly` | 1 с + 56 с + 7 с | 523 / 982 / 982 / 22 725, Σ і контрольна сума рівні. PASS |
| перерахунок doc 96 | POST /api/v1/documents/96/recalculate {periodKey} для 202609, 202610, 202611 (C:\ecr-wt\b12-bld\recalc-doc.ps1) | 27 с | 202610/202611 PASS; **202609 — 422 ECR-CALC-4221 «Period 202609 is closed… Reopen it first»** |
| Consistency | POST /api/v1/consistency/run {reason} (System.RunJob) (C:\ecr-wt\b12-bld\consistency-run.ps1) | 11 с | **R1 CALC_RESULT_VERSION_NOT_EFFECTIVE = 29 (усі 202609, doc 96, 296 результатів, V1 ≠ чинна V2) — FAIL проти очікування «R1 = 0»**; R3 EFFECTIVE_VERSION_FORMULAS = 0; R8 OUTPUT_WITHOUT_BINDING = 11 (info) |

\* вода: на live у T7b_00 doc 96 немає введених значень (water cells 0); на копії для доказу додано тестові входи (G:\EcrData\b12reh\seed-water-test.sql: 202610/202611 S1_6, S2_7 + об'єм R7002002), на live їх НЕ додавати.
Після: CategoryRuleRejected 6→0; MissingInputs 73, StepError 14 без змін; RowInactive +2 (S3_9 вода, прийняте відхилення); результати 96: 510→514; висячих 0.
⛔ Відкрите: 202609 закритий — R1 ×29 зникне лише після Reopen 202609 з причиною (слово людини) + перерахунку, або R1 лишається як відоме відхилення.
## ✎ 25.11 06:09 слово людини: крок 3.5 — ПЕРЕВІДКРИТИ doc 96 / 202609 (причина: «b12: перерахунок на нових версіях методологій, слово людини 25.11»), перерахувати 202609–202611; очікування R1 = 0.

- ✎ LIVE 25.11 06:48 (координатор, рішення (a)): +4 MissingInputs на T7b_00 doc 96 (S1_6/S2_7 × 202610/202611) — вода тепер методологія, на live немає введених концентрацій/об'єму; коректне «бракує даних» (202609 мав 3 такі рядки ще з V1). Компаратор Land FAIL лише через це; прийнято.

- ✎ LIVE 25.11 06:53: після Reopen 96/202609 і перерахунку — R1 0, R3 0, R8 11 (info). Але StepError 202609: 0 → 6 (#N/A: T11_03 LTD-A41 C12_02; T5_08 LTD-A08 C07_07; T5_09 LTD-A09 C07_08; T9_05 LTD-A16 C11_13; T9_09 LTD-A20 C11_11_01; T9a_19 LTD-A27 C11_11_02). До вікна 202609 мав застарілі результати V1 (R1 ×29), тому ці помилки не показувалися. Питання координатору; тимчасові акаунти вимкнено, WINDOW-ACTIVE лишено.

- ✎ 25.11 07:2x розбір 6 StepError 202609 (doc 96): значення вересня до/після 64/64 рівні (0 різниць); #N/A — виходи для поєднань категорія×речовина, яких немає в мапах коефіцієнтів (напр. C07_07 Gasoline: EF_2732/2735/2754/333 без ключа Gasoline). V1/старий шлях цих значень теж не давав. Вересневий прогін був 13.11, до B66 (d026d1899, 15.11) — тому помилки кроку не записувались. Той самий шаблон уже є в жовтні/листопаді (T9a_19 LTD-A27). Не прогалина даних і не дефект конверсії значень; шум діагностики — картка.
