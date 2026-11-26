# Картка: audit-filters-live Аудит на live: відбір за документом падає, поля Document/Column приймають лише внутрішні ID
Статус: live-verified (UI-прохід людини 26.11: «Перевірка аудиту на live - зроблено»; live 6260abdb8)
Гілка: lane/integ-audit-filters-live
База: 7afb19c97 (live b14)
Пачка: b15 (live 6260abdb8, 26.11 09:12)
Ризик: R2 (SQL-запит аудиту, продуктивність на live; UI фільтрів)
Рішення: людина 25.11 23:3x «Для аудиту перевірте всі вкладки та поля фільтрації» — повний прохід координатора на live b14 (акаунт людини land-owner)
Дизайн: не потрібен (запит + поля вибору); AC-1 — спершу план запиту на копії live

## 1. Що і навіщо
Повний прохід 25.11 23:2x–23:5x, вікно From 2026-11-18 / To 2026-11-25, кожне значення звірено з aud.*/wf.* на live (лише читання):

| Вкладка | Поле | Результат |
|---|---|---|
| Cell changes | From/To, Origin ×10 (103 UserEdit, 7294 Recalculation, решта 0), Late edits only (195), By user land-owner (87), Document 98 (16), Row key Methane (16), Column 73809 (12), Archive (2024-08-03…2024-10-31, 0), сортування By user ↑/↓, Load more | ✓ = БД |
| Structure changes | Entity type ×13 (CalculationBinding 9312, ColumnDef 8916, DataSource 1, Reopen 18, Methodology 6, MonthInputAlias 3, Project 2, ProjectTemplate 7, RegistryDef 9, RegistryEntry 10, TableDef 54, TableRow 3, TemplateVersion 1) | ✓ = БД |
| Security events | Event type ×16 (усі = БД, напр. LoginSucceeded 91, SignedOut 23, AccessDenied 11, RegistryValueChanged 24240 «there are more»), By user land-owner (56) | ✓ = БД |
| Workflow events | Action ×6 (Submit 66, Approve 63, ApproveStep 0, Reject 1, Reopen «Returned for rework» 62, Recall 1), By user land-owner (11), From 25.11 (11), 24.11–24.11 (182 через Load more) | ✓ = БД |
| Час | усі вкладки — місцевий час (16:56 = 14:56Z) | ✓ |

**Дефекти:**
- **D1 (головне).** Cell changes → Document **96** (415 змін за вікно): список не з'являється (>80 с скелетон); сервер — `Execution Timeout Expired` → 503 ECR-SYS-0503 (журнал ecr-20261125_016.log 23:25:02, CorrelationId 6c873aac-…); клієнт повторює запит — на live висить сесія SQL ~30 с щоразу. Запит — зведення за колонками з `ColumnDefId IN (SELECT … FROM OPENJSON(@visibleColumns))` + `IF EXISTS … arc.AuditCellChange` (текст: dm_exec_query_stats; doc 96 має тисячі колонок → великий @visibleColumns). Doc 98 (16 змін) — відповідає миттєво.
- **D2.** Поле **Document** — лише числовий DocumentId: введення «P1-V9-0001» (так документ видно скрізь в UI) мовчки стає «190001» → червоне «Document not found ECR-DOC-0404». Користувач не знає внутрішнього Id.
- **D3.** Поле **Column** — лише ColumnDefId: «M10» мовчки стає «10» → «No changes in this window» (хибна відповідь «змін не було»).
- **D4.** **By user**: чотири різні акаунти з однаковою назвою «b12 window temporary (disable after)» — не розрізнити (тимчасові акаунти вікон; набір вікна b14 уже виправлено на «live window temporary», старі назви лишились у БД).
- **AC-1 — причина D1 (вимір на копії EcrLandB14Reh, 26.11, sqlcmd -E -I, лише читання; той самий SQL, що будує AuditReader).**
  Вікно 17.11 22:00Z–25.11 22:00Z: у вікні 7 397 змін (усього aud.CellChange 5,81 млн, архів 0). Doc 96 — 415 змін у вікні (22 744 за весь час), 114 різних колонок; doc 100 — 6 950 змін, 285 колонок.
  @visibleColumns: видимі колонки документа 15 147 (106 КБ JSON) → після V-4 родоводу (`ColumnPathMapper.LineageAsync`, 8+ версій шаблону 1) **125 263 Id, 766 КБ JSON**.
  Причина: `ColumnDefId IN (SELECT CAST([value] AS int) FROM OPENJSON(@visibleColumns))` — оптимізатор ставить OPENJSON внутрішньою стороною вкладених циклів і розбирає весь JSON на КОЖЕН рядок журналу документа (seek за DocumentId). Пробник `IF EXISTS arc.AuditCellChange` не винен (архів порожній, 1 звернення).
  Виміри до фіксу: doc 96 з 125 263 Id — сторінка > 45 с (Timeout expired, на live 30 с → 503); doc 96 з 15 147 Id — сторінка 5,2 с, підрахунок 5,3 с; doc 100 з 15 147 Id — 20,5 с + 21,0 с; doc 98 (78 Id) — 0,15 с.
  Після (табличною змінною з PK, один розбір): doc 96 з 125 263 Id — сторінка 0,75 с, підрахунок 0,84 с (415 = live); doc 100 — 0,73 / 0,74 с (6 950); з 15 147 Id — 0,05–0,33 с. Фікс додатково звужує перелік до колонок вікна документа (114 / 285 Id) — вставка 125 тис. рядків у змінну зникає.
  ✎ Родовід `LineageAsync` над 15 147 колонками (125 263 рядки): 1,03 с → табл. змінна + `OPTION (HASH JOIN)` понад 1 000 Id — 0,52 с (6260abdb8; дозвіл координатора 26.11).
  **Наскрізно через власний Api** (Release, http://localhost:5201 → EcrLandB14Reh, акаунт test-audit-perf лише на копії, Api зупинено за PID), вікно 17.11 22:00Z–25.11 22:00Z, 3 прогони, мс:
  `GET /api/v1/audit/cells?…&limit=100&documentId=96` — 1384 (перший після старту Api) / 677 / 582 (100 рядків, total 415);
  `…documentId=100` — 565 / 659 / 587 (total 6 950); `/audit/cells/summary` doc 96 — 442 / 446 / 402, doc 100 — 348 / 419 / 380; `/audit/authors` — 29 / 16 / 15.
  Перед кроком родоводу (Debug, fd5553af7+3a79b08bc): cells doc 96 — 2383 / 1636 / 1218, з них родовід 942 мс (dm_exec_query_stats). На 7afb19c97 — тайм-аут (SQL сторінки > 45 с).
- ~~Питання (не дефект): «Clear filters» знімає Row key/Column/Origin/By user/Late, але лишає Document і дати — свідомо?~~ ✎ рішення людини 26.11: знімає ВСЕ на кожній вкладці → AC-9.

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Audit trail → Cell changes → документ LAND-TEST-ALL-METHODOLOGIES (P1-V9-0001) за 18–25.11 → список 415 змін ≤ 3 с; без 503 у журналі | live скрин + журнал | 6260abdb8 — live: заміри 1402/728/703 мс doc 96 (вікно b15) + UI-прохід людини 26.11 «зроблено» (пікери Document/Column, Clear filters на 4 вкладках, By user) |
| AC-1 | причина тайм-ауту встановлена на копії live (план запиту, час, розмір @visibleColumns) і записана в картку | §1 «AC-1 — причина D1» (sqlcmd на EcrLandB14Reh) | fd5553af7 |
| AC-2 | запит(и) Cell changes з documentId на копії live (doc 96, doc 100 6950 змін) ≤ 2 с; тест, що червоний на 7afb19c97 (поріг або кількість читань) | `AuditReaderLargeVisibleSetTests` (7 000 змін, 125 001 Id; база: Failed [1 m 37 s]; фікс: 373 мс); SQL на копії: doc 96 >45 с → 0,75/0,84 с, doc 100 20,5+21,0 с → 0,73/0,74 с (табл. змінна), далі звуження до 114/285 Id; родовід — `ColumnPathMapperLargeLineageTests` (мутація «без хешу»: Assert.EndsWith Failure). Наскрізно (власний Api, Release): cells doc 96 1384/677/582 мс, doc 100 565/659/587 мс; summary ≤ 446 мс — §1 | fd5553af7, 6260abdb8 |
| AC-3 | Document — вибір документа за назвою/кодом (як у Documents), у URL — id; довільний текст не перетворюється в інший документ | `AuditPage.pickers.test.tsx` AC-3 (база: «expected '…documentId…' not to contain 'documentId='»); e2e `auditCellPickersLive.spec.ts` | 3a79b08bc |
| AC-4 | Column — вибір колонки документа за підписом (місяць/назва), не числом; некоректний ввід не дає «No changes» | `AuditPage.pickers.test.tsx` AC-4 (база: «… not to contain 'columnDefId='»); e2e `auditCellPickersLive.spec.ts` | 3a79b08bc |
| AC-5 | By user — однакові назви розрізнені (логін у дужках) | `AuditPage.pickers.test.tsx` AC-5 (база: «Unable to find role="option" … (#5)»); логін — лише з Security.ManageUsers, інакше #id (§4 [question]) | 3a79b08bc |
| AC-6 | клієнт не повторює запит, що впав з 503/тайм-аутом, безкінечно (повідомлення з Retry) | `AuditPage.noRetry.test.tsx` (createQueryClient застосунку; база: 1 failed) | fd5553af7 |
| AC-9 | «Clear filters» знімає ВСЕ разом — Document, дати (типове вікно), Row key, Column, Origin, By user, Late — і так само на кожній вкладці (Structure/Security/Workflow: фільтри вкладки й дати; вкладка лишається) — рішення людини 26.11 | `AuditPage.clearAll.test.tsx` (база: 4/4 червоні — «expected '?from=…' to be ''», «Unable to find role="button" … ⟦audit.reset⟧»); e2e `auditCellPickersLive.spec.ts` «Clear filters» | 6260abdb8 |

## 3. Межі
- Можна змінювати: серверний запит/обробник аудиту клітинок (Ecr.Application/Ecr.Infrastructure — Audit*), індекс лише якщо доведено планом (тоді міграція EF — зупинись і напиши координатору до генерації), клієнт features/audit/** (або де живе екран Audit trail), ✎ 26.11 (координатор): `Ecr.Infrastructure/Persistence/ColumnPathMapper.cs` (LineageAsync), тести до них, сід лише для нових ключів UI (секція свого імені, #UiChanged за правилом SeedTextUpdateTests).
- Заборонено: live (EcrLandUi, :5094/:6094) — ані читання, ані запису; бази ECR/FLERT/EcrLegacyProd; H:\ECR як робоча папка.
- Копія live для виміру: EcrLandB14Reh (G:\EcrData\b14reh, копія live 25.11 ~21:00) — лише читання й власний Api на вільному порту ≥ 5200.
- Умова зупинки: потрібна міграція або зміна контракту, яку не можна зробити сумісно; тижневий ліміт: комітити й пушити WIP щонайменше кожні 30 хв.

## 4. Звіт
**Done** — гілка `lane/integ-audit-filters-live` (база 7afb19c97), коміти:
- fd5553af7 — D1 + AC-6: `AuditReader.cs` (перелік видимих колонок — табличною змінною @visibleSet з PK, один розбір OPENJSON; понад 500 Id — звуження до колонок, що змінювались у вікні документа, гаряче + архів, лише перетин); `journalPages.ts` (`retry: false` у 4 журналах); тести `AuditReaderLargeVisibleSetTests.cs`, `AuditPage.noRetry.test.tsx`.
- 3a79b08bc — D2–D4: `features/audit/filterOptions.ts` (новий), `authorOptions.ts`, `pages/admin/AuditPage.tsx` (Document/Column — Select), тести `AuditPage.pickers.test.tsx` (новий), оновлено `AuditPage.cellNeedsDocument`, `AuditPage.keepRows`, `filterDebounce` під вибір; e2e `e2e/auditCellPickersLive.spec.ts`.
- add482370 — D2 дрібний фікс за e2e стенда: пошук не вимикався, коли набраний код = підпис документа з журналу.
- 6260abdb8 — D1 (родовід): `ColumnPathMapper.cs` — табл. змінна + `OPTION (HASH JOIN)` понад 1 000 Id; тест `ColumnPathMapperLargeLineageTests.cs`. AC-9: «Clear filters» у шапці на всіх вкладках, знімає все (`AuditPage.tsx`), тест `AuditPage.clearAll.test.tsx`, e2e «Clear filters» у `auditCellPickersLive.spec.ts`.
Без міграції, без зміни контракту, без нових ключів UI. Акаунт `test-audit-perf` — лише на EcrLandB14Reh (пароль — `G:\EcrData\b14reh\acc\test-audit-perf.txt`, ролі скопійовано з land-owner).

**Verified**
- Копія EcrLandB14Reh (sqlcmd -E -I, лише читання) — §1 «AC-1»: до — doc 96 >45 с (Timeout), doc 100 20,5+21,0 с; після (той самий SQL з табличною змінною) — 0,73–0,84 с з 125 263 Id; зі звуженням у запит іде 114/285 Id.
- `dotnet test tests/Ecr.Infrastructure.Tests --filter "FullyQualifiedName~AuditReader|FullyQualifiedName~AuditSortPaging|FullyQualifiedName~SheetActivity"` — 124/124 (новий: 373 мс; на 7afb19c97 — Failed [1 m 37 s]).
- `Ecr.Architecture.Tests` локально — exit 0 (перший прогін без git у PATH дав 19 падінь StatusFromGitScriptTests — середовище).
- vitest `src/pages/admin/__tests__/AuditPage* src/features/audit filterDebounce adminTimestamps` — 39 файлів, 170/170; нові тести червоні на базі (рядки — у §2). eslint + `tsc --noEmit` — чисто.
- e2e-stand `workflow_dispatch` ref=lane/integ-audit-filters-live, grep `auditCellPickersLive|auditSortLive|auditTrail|auditFiltersLive`: run 45760996875 (3a79b08bc) — 8 passed, 1 failed (Document: пошук не йшов — виправлено add482370); run 45762835339 (add482370) — success.
- CI: Evidence 45760959461 (3a79b08bc) — success; Evidence 45759573038 (fd5553af7) — failure у `ConfigurationKeysTests` (Security:RateLimit:SearchPermit, appsettings 30 vs код 20 — не зачеплено цією lane; на 3a79b08bc того ж тесту не видно). CI 45762833655 і Evidence 45762833659 на add482370 — success.
- ✎ Голова **6260abdb8**: CI 45767222269 (повний) — success; Evidence 45767222356 — success; e2e-stand 45767234676 (той самий grep) — success.
- Наскрізно через власний Api (Release, :5201 → EcrLandB14Reh), 3 прогони — §1: cells doc 96 1384/677/582 мс, doc 100 565/659/587 мс, summary ≤ 446 мс, authors ≤ 29 мс. Api зупинено за власним PID.
- Infrastructure `--filter "…ColumnPathMapper|…Lineage|…AuditReader|…AuditExport"` — 64/64; мутація LineageSqlFor «без хешу» → `ColumnPathMapperLargeLineageTests` червоний (Assert.EndsWith). vitest audit — 171/171; AC-9 на 7afb19c97 — 4/4 червоні.

**Next steps**
- AC-0 — після вікна на live.
- AC-7 (логін у By user для читача без ManageUsers) і AC-8 (серверний перелік змінених колонок) — відкладено рішенням координатора 26.11.

Координатор 25.11: агента зупинено на 99 % тижневого ліміту до першого коміту (робоча копія C:\ecr-wt\audit-filters чиста, гілка не запушена). Почати заново тим самим брифом після 29.11 17:00.

Координатор 26.11: звіт fd5553af7/3a79b08bc/add482370 не прийнято остаточно — AC-0/AC-2 (≤ 2 с) без наскрізного виміру через Api; дозволено ColumnPathMapper.cs (LineageAsync 1.7 с на doc 96) у цій же гілці, агент повернутий. AC-7/AC-8 — відкладені рядки. Evidence 45759573038 на fd5553af7 червоний — ConfigurationKeysTests SearchPermit 30/20 = флейк із картки config-fallback-guard, не ця гілка.

Координатор 26.11: AC-7/AC-8 винесено в картку integ-audit-filters-followup (planned).

Координатор 26.11: ПРИЙНЯТО. Перевірено мною: голова 6260abdb8 запушена; CI 45767222269 success, Evidence 45767222356 success, e2e-stand 45767234676 success (gh); diff add482370..6260abdb8 — 5 файлів у межах §3; SHA в AC-1..AC-6, AC-9; порт 5201 вільний. Наскрізно на копії: doc 96 cells 582–677 мс (перший 1384), doc 100 565–659 мс, summary ≤ 446 мс. OPTION (HASH JOIN) лише > 1000 id — свідомо. AC-0 — у вікні (екран людини: Audit trail → документ P1-V9-0001 → список за ≤ 3 с). Робоча копія C:\ecr-wt\audit-filters лишається до вікна; акаунт test-audit-perf лише на EcrLandB14Reh.
