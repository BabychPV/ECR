# Картка: documents-board-yearly Дошка документів і стан аркуша — під річний документ
Статус: live-verified
Гілка: lane/integ-documents-board-yearly
База: d75032896 (origin/lane/integ-b13)
Пачка: b14 (SHA в AC — cherry-pick пачки lane/integ-b14 → live 7afb19c97)
Ризик: R1
Рішення: людина 25.11 (два скрини) «це не зрозумів чому»
Дизайн: не потрібен

## 1. Що і навіщо
- Documents → Board: усі 5 документів у стовпці «No state for this period», хоча лічильники над дошкою кажуть «4 Draft · 1 Partly submitted». Причина: `documentsBoardModel.ts` розкладає за `sheetStates` обраного періоду (модель D-93, місячна), а сторінка тепер річна — періоду немає, станів немає, усе падає в «без стану».
- Документ → «Sheet: Draft» без місяця: незрозуміло, чий це стан (документ «Partly submitted», вересень перевідкрито).

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Documents → 2026 → Board — документи в тих самих стовпцях, що й лічильники (Draft 4, Partly submitted 1); стовпця «No state for this period» немає | live скрин | 8e39b3c4a — live не перевірено агентом (заборона live); замінник — e2e `documentsBoardYear` на CI-стенді (знімок `documentsBoardYear-board-1440` у звіті Playwright) |
| AC-1 | дошка й лічильники рахуються з одного джерела (річний стан документа); стовпці: Draft · Partly submitted · Submitted/Waiting · Returned · Approved | vitest | 8e39b3c4a (`documentsBoardModel.test.ts` AC-1; `DocumentsPage.board.test.tsx` AC-1 — на базі червоний: `expected '0' to be '4'`) + e2e 411946fb9/f791702be |
| AC-2 | документ: біля «Sheet:» — стан із місяцем («Draft · September 2026 (reopened)») або річний стан документа; без голого «Draft» | vitest + e2e | 8e39b3c4a (`DocumentProgress.stateScope.test.tsx` — на базі 2 червоні; `DocumentPage.sheetStateScope.test.tsx` AC-2a/b/c) + e2e 411946fb9 |

## 3. Споживачі старої місячної моделі (grep `sheetStates|periodState|documentState`, перелік за рік)
| Споживач | Що брав | Рішення |
|---|---|---|
| `features/documents/documentsBoardModel.ts` (`boardColumns`) | `documentState(sheetStates)` — за рік словник порожній → усе в `NoState` | **виправлено**: `documentYearState(periodStates, розпочаті періоди)` |
| `features/documents/DocumentsBoard.tsx` | бейдж картки — `documentState` | **виправлено** (той самий стан року); смужка аркушів за рік — «—» (станів аркушів за рік немає) |
| `features/documents/DocumentQuickLook.tsx` (шторка «око») | бейдж стану — `documentState(рядок переліку)` → за рік без бейджа | **виправлено** (`documentYearState`); блоки місяця — як були, з картки за поточний період |
| `features/documents/DocumentListSummaryStrip.tsx` | сервер `GET /documents/summary?year=` | без змін — це «друге» джерело; правило року клієнта — дзеркало `SummarizeYearAsync`/`WhereYearState` |
| `features/documents/DocumentYearStrip.tsx` / `yearStripModel.ts` | `periodStates` + календар | без змін (уже річне) |
| `pages/DocumentPage.tsx` → `DocumentProgress` («Sheet:») | `sheetStates` якоря (січень) для документа «рік» | **виправлено**: «рік» — зведений стан аркуша за розпочаті місяці (`submission-target.monthColumns`) + «Year N»; копія на місяць — назва місяця (+ «Reopened for this document») |
| `findHse400Document.ts`, `documentLock.ts`, `DeleteDocumentAction`, `BusinessKeyChangeAction`, `useDocumentPeriodReopen`, `SheetActions`, `SheetLiveRefresh` | картка документа ЗА ПЕРІОД (`GET /documents/{id}?periodKey=`) | без змін — працюють із карткою періоду, не з рядком переліку року |

## 4. Звіт виконавця
- Гілка `lane/integ-documents-board-yearly`, база d75032896. Коміти: 8e39b3c4a `[CODE]` (код + vitest), 411946fb9 `[TEST]` e2e `documentsBoardYear`, f791702be `[TEST]` знімки вкладенням звіту.
- «Одне джерело»: сервер не віддає річного стану рядка (контракт не чіпав). Дошка рахує стан року з `periodStates` рядка (сервер, той самий CASE по аркушах) по РОЗПОЧАТИХ періодах календаря — правило дослівно як у `SummarizeYearAsync`/`WhereYearState` (`documentYearState`/`yearStateOfPeriods`, `documentSheets.ts`). Обмеження: поки календар проєкту в дорозі, Scheduled-місяці (сервер віддає їх `Draft`) входять у рік — стан може на мить бути «Partly submitted» замість «Submitted».
- Стовпці дошки: Draft · Partly submitted (новий стовпець, ключ `documents.statePartlySubmitted` уже був) · Waiting · Returned or rejected · Approved (+ No approval / No state — лише коли є).
- Локально: `tsc -b` 0, `npm run build` ок, eslint ок, vitest `pages/__tests__/DocumentPage*`, `DocumentsPage*`, `features/documents` — 164 файли / 942 тести зелені.
- Червоний доказ: AC-1 на базовому коді — `expected '0' to be '4'`; AC-2 (`DocumentProgress.stateScope`) на базовому — 2 червоні. ⚠ `DocumentPage.sheetStateScope` на базовому коді не падав «чисто», а висів на таймаутах тесту (400 с) — тому червоний доказ AC-2 — юніт-тест компонента.
- CI lane: run 45654585042 на f791702be — success (попередній 45649324644 на 411946fb9 скасовано новим пушем). Evidence 45654585138 — success.
- e2e-stand (workflow_dispatch, grep `documentsBoardYear`): run 45654612091 — 2 passed (знімки `documentsBoardYear-board-1440`/`-sheet-1440` — у артефакті `e2e-stand-report-shard-1`).
- Next: [debt] дошка за рік не має смужки аркушів (лише «—»); можна показати 12-місячну смугу як у таблиці.

Координатор 25.11: прийнято (CI 45654585042, e2e 45654612091 success на f791702be).

Координатор 25.11 23:1x live b14 (7afb19c97, акаунт людини land-owner): Documents → 2026 → Board — Draft 3 (P4, P6, P7) · Partly submitted 2 (P1-V9-0001, LAND_UI-P1-0001) = лічильники «3 Draft · 2 Partly submitted»; стовпця «No state» немає. Doc 96: «Sheet: Partly submitted · Year 2026». AC-0 PASS → live-verified.
