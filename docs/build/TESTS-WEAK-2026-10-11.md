# Слабкі докази: тести ФВ-5.15 / 0.2 / 5.13 / 5.14 / 4.3 / 1.15 — 11.10.2026

Виконавець: розробник тестів (сабагент). Вихідний звіт: `REQ-COVERAGE-2026-10-11.md` (§5, §8 P0/P1).
Гілка: `lane/tests-weak-evidence`, **лінійна, 5 комітів `[TEST]`**, база `eb34f57d7` (`origin/dev/integration` на старті; далі гілка встигла піти вперед — зведення cherry-pick'ом, rebase запушеної гілки без force неможливий). Продуктовий код **не змінювався**.

## 0. Підсумок

| | |
|---|---|
| Вершина lane | `4d5dea7e6` |
| CI lane на вершині | **CI — success** (31 хв; усі завдання зелені, у т. ч. `server`, `client`, `a11y (dark)`, `a11y (light)`, `server-part (api-1/api-2/core/scenarios)`, `windows`, `msi`; `pr-source`/`shuffle` — skipped), **Evidence — success**; run `38160955606` / `38160955616` |
| Нові тести | 6 нових файлів (5 класів тестів + 1 partial-сторож), 44 нових прогони (з них 15+5+11 рядків теорій); 1 тавтологічний тест видалено |
| Мутаційні докази | усі нові тести червоніють на мутації (§3); мутації — у власному detached-worktree `F:\ecr-wt\tests-weak-mut`, нічого не закомічено |
| Знайдено дефект продукту | **ФВ-0.2: зміна `RowDef.Ordinal` опублікованої версії міняє суму діапазону в рантаймі (60 → 1039).** Червоний тест у гілку НЕ включено (§4) |

## 1. Що додано (файли, тести)

### 1.1 ФВ-5.15 (D-219 «Rejected як Draft») — комміти `171e43e61`, `8c0b0b0da`

**`tests/Ecr.Infrastructure.Tests/Reporting/ReportSnapshotSheetStatusTests.cs`** (SQL, реальна база; 18 прогонів):
- Теорія з 15 комбінацій станів аркушів одного періоду (`none`/`Draft`/`Rejected`/`Submitted`/`Approved` і суміші) → очікуваний статус зрізу через `RefreshStatusAsync`. Ключові рядки: `Submitted,Rejected`, `Approved,Rejected`, `Submitted,Approved,Rejected` → `Draft`.
- «Відхилений аркуш одного періоду робить річний зріз чернетковим, але не чіпає сусідній період» (річний/періодний зрізи).
- «Після повторного подання відхиленого аркуша зріз знову подається» (Rejected не термінальний).
- Наскрізно через `BuildAsync` → `rpt.v_WaterReport_v1`: зріз «Submitted + Rejected» будується `Draft`, рядок **не видно** регулятору; після повторного подання той самий рядок зʼявляється зі `Status = 2` (контроль «не порожня вʼюха»). Описом `WaterReport` — «знайти або створити» (код унікальний, його створює й `ReportSnapshotBuilderTests`).

**`tests/Ecr.Application.Tests/Security/RejectedSheetEditRulesTests.cs`** (юніт, 10 прогонів): `Rejected` редагується як `Draft` (закритий період/архів/Read усе одно блокують); подається знову (Submitted/Approved — ні; блокувальні помилки валідації — так); не затверджується й не «відкривається» (`BusinessRule`); збіг рішень із `Draft` за кожного з 5 рівнів гранта.

**`tests/Ecr.Api.Tests/RejectedSheetHttpFlowTests.cs`** (HTTP, 1 потік): Draft PATCH 200 → Submit 204 → PATCH 403 `DocumentSubmitted` (ФВ-5.13) → `approved:false` без причини **422 `ECR-DOC-0422`**, стан не змінився → з причиною 204, `Rejected`, `RejectedReason` збережена → approve на Rejected 403 `BusinessRule` → **PATCH на Rejected 200** (D-219) → повторне подання (причина скинута, у `calc.SubmissionSnapshot` нова версія з виправленим значенням) → approve 204 → PATCH 403 `DocumentApproved`, версії подання не змінилися (ФВ-5.14).

### 1.2 ФВ-5.13 — `8c0b0b0da`
`SubmitEditRaceTests` (два SQL-тести гонки подання/правки): додано `Trait("Requirement","ФВ-5.13")` до наявного `ФВ-9.4`; тіла не чіпано. Прогін **20/20** локально (правило «N/20»; зміна лише трейтова).
Додатково «Submitted заморожено» тепер має HTTP-доказ (PATCH 403 у потоці вище).

### 1.3 ФВ-5.14 — `171e43e61`, `8c0b0b0da`
SQL: усі аркуші `Approved` → зріз `Approved` (рядки теорії `Approved`, `Approved,Approved`); HTTP: після Approve PATCH 403, `calc.SubmissionSnapshot` без змін. ⚠ Див. §5: «незмінна версія» у коді фіксується на **Submit**, а не на Approve — це розбіжність ТЗ↔код, не тесту.

### 1.4 ФВ-0.2 — `e3bc2464a`
- **Видалено** тавтологічний `RangeExpansionTests.Зміна_Ordinal_після_публікації_НЕ_змінює_результат_формули`.
- **`tests/Ecr.Application.Tests/Recalculation/RangeSurvivesReorderTests.cs`** (2 тести, обидва з трейтом ФВ-0.2): (а) одиничне посилання `[Main].[7001002].[Jan]` у `SliceEvaluationContext` читає той самий рядок до й після `Reorder`; (б) граф залежностей діапазону (`DependencyExtractor`) несе ключі й `SortOrder` = лічильник, а не значення `Ordinal` (Ordinal 10/20/30/40).
- Червоний тест на діапазон — не в гілці, див. §4.

### 1.5 ФВ-4.3 (previewToken) — `4d5dea7e6`
**`tests/Ecr.Api.Tests/ImportPreviewTokenApiTests.cs`** (HTTP + БД, 3 тести): (1) невідомий і порожній токен → 422 `previewExpired`, дані не змінені; (2) токен іншого користувача з тими самими правами → 422, власний → 200, повтор → 422 (токен спожитий), значення в БД перевірені на кожному кроці; (3) рядки книги в **іншій позиції** з тим самим `RowKey` змінюють саме свій рядок (карта, не номер рядка).
Трейт ФВ-4.3 додано до реальних доказів: 3 негативи токена в `ExcelImporterFileRejectionTests` (прострочений, чужий, чужий документ) і `ImportRoundTripScenarios` (перегляд→apply). Слабкі трейти (`WorkbookMapChunkTests`, `ImportPreviewStoreTests`) не прибирав — не мої; лише додав справжні.

### 1.6 ФВ-1.15 — `b53614a20`
**`tests/Ecr.Architecture.Tests/PrincipleTests.ProjectDeletionBypass.cs`** (partial до `PrincipleTests`; 1 сторож + теорія з 11 рядків): увесь `src/**/*.cs` без коментарів — `Projects|Periods …ExecuteDelete[Async]`, `Projects|Periods.Remove[Range]`, `DELETE/TRUNCATE doc.Project|doc.Period` у C#-рядках. Теорія доводить, що регулярки ловлять обхідні форми і не чіпають `ExecuteDelete` по інших таблицях та `doc.PeriodAccess`. Захисту в БД (DENY/тригер) це **не замінює**.

## 2. Локальні прогони (режим «лише цільове»)

| Набір | Результат |
|---|---|
| `dotnet build` Infrastructure/Application/Api/Architecture/Expressions/Adapters.Tests | 0 помилок |
| `Ecr.Architecture.Tests` (повний) | 645/647; 2 падіння — `StatusFromGitScriptTests.Відмова_замість_вигаданого_звіту(git/outfile)`, середовищні (PATH з git), до моїх змін не стосуються; ФВ-1.15 і решта зелені |
| `ReportSnapshotSheetStatusTests` | 18/18 |
| `RejectedSheetEditRulesTests` + `RangeSurvivesReorderTests` | 10/10 |
| `RejectedSheetHttpFlowTests` | 1/1 (~20 с) |
| `ImportPreviewTokenApiTests` | 3/3 |
| `RangeExpansionTests`, `ExcelImporterFileRejectionTests` | 6/6, 6/6 |
| `SubmitEditRaceTests` | **20/20** |

Нові тести не залежать від таймінгу/черг; потік `RejectedSheetHttpFlowTests` і `ImportPreviewTokenApiTests` послідовні (без `Task.Delay`/гонок).

## 3. Мутаційні докази

Власний detached-worktree `F:\ecr-wt\tests-weak-mut` (знятий; мутації не комітились). Два заходи збірки+прогону. «Червоніє» = перелічені тести падають, решта класу — ні, де це не зазначено.

| # | Мутація (файл) | Червоніє |
|---|---|---|
| M1 | прибрати `or DocumentStatus.Rejected` в `ReportSnapshotBuilder.StatusOfDataAsync` (`:1209`) | **6 з 18** `ReportSnapshotSheetStatusTests`: теорія `Submitted,Rejected`, `Approved,Rejected`, `Submitted,Approved,Rejected` (Expected Draft / Actual Submitted); «річний/сусідній період»; «після повторного подання»; **«не потрапляє в регуляторну вʼюху»** |
| M2 | `EditRules.CanEdit`: `case Rejected → Deny(DocumentSubmitted)` | `RejectedSheetEditRulesTests`: «редагується як Draft», теорія збігу з Draft (усі 5 рівнів); **HTTP-потік** — PATCH на Rejected `403` замість `200` |
| M2b | `EditRules.CanSubmit`: `… or Rejected` у відмові за станом | «подається знову»; теорія збігу з Draft |
| M2c | `EditRules.CanApprove`: `!= Submitted` → `is Draft` | «не затверджується і не повертається» + теорія (Approve, Manage) |
| M3 | `ApprovalState.Reject`: прибрати перевірку коментаря | **HTTP-потік** — `204` замість `422` (крок «без причини») |
| M4 | `ExcelImporter.LoadPlanAsync`: прибрати перевірку власника токена | `ImportPreviewTokenApiTests.Токен_чужого_користувача…` (чужий токен застосовується) |
| M5 | `ExcelImporter`: не видаляти токен після `apply` | той самий тест, крок «повтор»: 409 `ECR-CELL-0409` замість 422 `previewExpired` |
| M6 | `SliceEvaluationContext.Read`: одиничне посилання за позицією (`OrderBy(Ordinal).ElementAt(n)`) | `RangeSurvivesReorderTests.Одиничне_посилання…` (Expected 20 / Actual 999) |
| M7 | `DependencyExtractor`: `SortOrder = Ordinal` рядка | `RangeSurvivesReorderTests.Граф_залежностей…` (Expected [0,1,2] / Actual [10,20,30]) |
| M8 | додати в `src/Ecr.Infrastructure` `db.Periods.Where(…).ExecuteDeleteAsync()` | `PrincipleTests.Масове_видалення_проєктів_і_періодів_не_обходить_сторожа` (єдиний червоний з 19) |

Тавтологічний ФВ-0.2 тест міг пройти за будь-якої мутації — підтверджено читанням коду (після `Reorder` продукт не викликався).

## 4. Дефект продукту, знайдений при переписі ФВ-0.2 (потребує рішення/правки коду)

**Факт.** Формула з діапазоном `SUM([Main].[7001001:7001003].[Jan])` у рантаймі рахує **за поточним `RowDef.Ordinal`**, а не за ключами, розкритими при публікації:
- `SliceEvaluationContext.Read` (`src/Ecr.Application/Recalculation/SliceEvaluationContext.cs:145-155`) викликає `RangeExpander.Expand(table, from, to)` над знімком структури;
- `PATCH …/presentation` (`PatchPresentationHandler` → `TemplateVersionStore.PresentationColumns`: `("RowDef","Ordinal")`; `ChangeClassifier.PresentationFields` містить `Ordinal`) **дозволяє** змінити `Ordinal` рядка в ОПУБЛІКОВАНІЙ версії.
- Відтворення (знімок через `TemplateBuilder`, ті самі дані, лише `Reorder`): сума **60 → 1039** (рядок 7001004 = 999 потрапив у діапазон). Дані не зіпсовано — число інше: саме клас помилок, який ФВ-0.2 забороняє. Граф `cfg.FormulaDependency` (розкритий при публікації) при цьому розходиться з тим, що читає рантайм.
- Коментарі в коді хибні: «Діапазон розкривається ТУТ так само, як при публікації» і `RangeExpander`: «У рантаймі діапазонів не існує».

**Червоний тест** — `H:\ECR\.sync-local\ready\tests-weak-evidence\FV-0.2-runtime-range-reorder.repro.cs.txt` (клас `RangeRuntimeReorderTests`, кладеться в `tests/Ecr.Application.Tests/Recalculation/`). У гілку не включено: він червоний за змістом, а `honesty-guard` забороняє `Skip`.

**Варіанти виправлення (рішення — людини/«Аналізу», я код не міняв):**
1. Заборонити `Ordinal` рядка в презентаційному патчі опублікованої версії (прибрати `("RowDef","Ordinal")` з `PresentationColumns`; `ChangeClassifier` класифікує за іменем поля без сутності — `Ordinal` спільний для `ColumnDef`/`RowDef`, потрібна сутнісно-залежна класифікація). Найдешевше; ціна — порядок рядків у опублікованій версії міняється лише клоном версії.
2. Рантайм бере розкриті при публікації ключі з `cfg.FormulaDependency` (а не `RangeExpander` над поточним знімком). Змістовніше, зачіпає `SliceEvaluationContext`, `ValidationEngine` (там теж `RangeExpander`) і відкат/кеш знімка.
Після виправлення — кинути файл-відтворення в гілку; він має позеленіти без змін.

## 5. Що лишилось / не зроблено

- **ФВ-1.15, захист у БД** (`DENY DELETE` / тригер на `doc.Project`/`doc.Period` + SQL-тест): потребує DBA (тестові хелпери роблять `DELETE FROM doc.Period` — тригер їх зламає); у коді сторож тепер ширший, але не остаточний.
- **ФВ-5.14, «незмінна версія» ↔ код.** Фіксація (`calc.SubmissionSnapshot` і заморожування звітного зрізу, ФВ-9.17) відбувається на **Submit**; `Approve` лише перераховує статус зрізу (`ReportSnapshotSyncTests…не_морозить`); за кодом `ReportSnapshot.RefreshStatus` міняє статус будь-якого НЕ поданого зрізу, тобто `Approved`-зріз іще може піти назад (окремим тестом не перевірено). Тести перевіряють фактичну поведінку; чи вона відповідає «Approved фіксує незмінну версію» — питання до ТЗ/D-запису (у §6 REQ-COVERAGE цього рядка немає).
- **ФВ-4.3:** прострочення токена за часом (TTL) на рівні HTTP не перевірено (потрібен шов часу в `IImportPreviewStore`); лишається на юніт-рівні (`previewExpired`). Слабкі трейти `WorkbookMapChunkTests`/`ImportPreviewStoreTests` лишились — перетегування «зняти ФВ-4.3» вимагає рішення щодо `RequirementCensus`.
- **ФВ-1.12, ФВ-1.14, ФВ-11.5, ФВ-14.9b, ФВ-12.4a** — не мої (потребують D-запису або іншого власника).
- Lane стартує від `eb34f57d7`; `origin/dev/integration` пішов уперед (`805321d41` на момент пушу). Зведення — cherry-pick 5 лінійних комітів `171e43e61`, `e3bc2464a`, `b53614a20`, `8c0b0b0da`, `4d5dea7e6`; конфліктів не очікую (нові файли + дрібні трейти/видалення одного тесту).
- Обидва `StatusFromGitScriptTests` падали локально через PATH — на CI зелено.

## 6. Хеші

| Хеш | Зміст |
|---|---|
| `171e43e61` | ФВ-5.15: SQL-статус зрізу з Rejected + EditRules |
| `e3bc2464a` | ФВ-0.2: видалено тавтологію, два тести на фактичних шляхах |
| `b53614a20` | ФВ-1.15: сторож проти `ExecuteDelete`/сирого SQL |
| `8c0b0b0da` | ФВ-5.15/5.13/5.14: HTTP-потік + трейт ФВ-5.13 |
| `4d5dea7e6` | ФВ-4.3: HTTP-тести токена + трейти |

Прибрано: worktree `F:\ecr-wt\tests-weak-mut`, `F:\ecr-wt\tests-weak` (без `--force`; `node_modules`-junction'ів не було), 4 власні тестові бази `EcrTest_{Api,Infrastructure}_*` обох worktree.

## 7. Додаток (хвости інтеграції, 11.10)

- **ФВ-0.2 (§4)** — виправлено в `dev/integration` (`5a8791b0c`: рантайм читає ключі публікації); хвости — попередження/лічильник при запасному шляху, проводка через `RecalculationService`, Excel-експорт — у lane `lane/fu-fv02-tail`.
- **ФВ-5.14 (§5)** — розбіжність закрито записом `D-387` («ТЗ під код»): незмінна версія й заморожування зрізу — на `Submit`; `Approved`-зріз іде назад у `Draft` лише поки він НЕзаморожений (`Reopen` затвердженого аркуша, `W1_03`), заморожений `Submitted` не змінюється. Реального відкату зафіксованої версії немає.
- Слабкий підрядок `"20"` у `RejectedSheetHttpFlowTests` (крок 7) зроблено точним окремим комітом.