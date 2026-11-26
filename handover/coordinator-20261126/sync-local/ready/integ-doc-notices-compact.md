# Картка: doc-notices-compact Повідомлення над сіткою документа — один компактний рядок
Статус: live-verified
Гілка: lane/integ-doc-notices-compact
База: d75032896 (origin/lane/integ-b13)
Пачка: b14
Ризик: R1
Рішення: людина 25.11 (2 скрини doc 96 після перевідкриття січ–вер) «ці повідомлення багато забирають місця подумай як краще»; варіант обрав координатор
Дизайн: не потрібен (UI, макет нижче)

## 1. Що і навіщо
Зараз над сіткою doc 96: 3 блоки «Calculation for <місяць>: some rows are not included» (по 3 рядки + технічні деталі) + список із 9 рядків «<місяць> is reopened … until Nov 25, 11:59 PM» → сітка починається з другого екрана. Перевідкриття ще й дубльовано в шапці («Reopened for this document until …»).

Рішення:
1. **Перевідкриті місяці** — без окремого блоку. Лише чип у шапці: «Reopened: Jan–Sep 2026 · until Nov 25, 23:59» (суміжні місяці з однаковим строком згортаються в діапазон; різні строки — кілька діапазонів; підказка — повний перелік).
2. **Проблеми розрахунку** — одна смуга (висота 1 рядок), згорнута за замовчуванням: «Calculation: rows not included in 3 months — 20 formula failed · 76 missing input · 3 inactive  [Details]». Колір/іконка за найгіршою причиною.
3. **[Details]** розгортає одну таблицю: рядки = причини, стовпці = місяці (Sep | Oct | Nov), у комірках кількість + «Show rows»; технічні деталі — там же, згорнуті. Стан згорнуто/розгорнуто — на користувача (localStorage, try/catch).
4. Смуга й чип не зсувають сітку при оновленні (фіксована висота).

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live doc 96 (перевідкрито січ–вер, проблеми в вер/жов/лис), 1440×900: перший рядок сітки видно без прокрутки; над сіткою ≤ 2 рядки повідомлень | live скрин до/після | 2c4044727 — live не перевірено агентом (заборона live); замінник — e2e `docNoticesCompact` на CI-стенді (смуга ≤ 40 px, перший рядок сітки ≤ 900 px; знімки `docNoticesCompact-*-1440` у звіті Playwright) |
| AC-1 | перевідкриті місяці: один чип у шапці з діапазонами (суміжні згорнуто, різні строки — окремо), підказка з переліком; окремого списку немає | vitest | 2c4044727 (`ReopenedMonthsChip.test.tsx`, `DocumentPage.yearLayoutL7` L7c → чип) |
| AC-2 | проблеми розрахунку: одна згорнута смуга з сумами за причинами; Details → матриця причина × місяць, «Show rows» веде на ті самі рядки, що й зараз | vitest + e2e | 2c4044727 (`CalculationIssuesStrip.test.tsx` AC-2 ×2, `DocumentPage.yearCalcIssues`) + e2e f6ae371a2 (`docNoticesCompact` + 5 специфікацій B78 через смугу) |
| AC-3 | стан Details пам'ятається між відкриттями документа; без localStorage — згорнуто, без помилки | vitest | 2c4044727 (`CalculationIssuesStrip.test.tsx` AC-3) + e2e reload f6ae371a2 |
| AC-4 | a11y: смуга — region з назвою, Details — кнопка aria-expanded; темна/світла тема | a11y e2e | 2c4044727 (vitest AC-4) + e2e f6ae371a2 (axe light/dark по смузі) |
(AC-5 «updated by another user» — серверна причина; винесено в картку integ-stale-banner-audit.)

## 3. Споживачі старих компонентів повідомлень (grep `document-calc-issues`, `YearReopenedBanner`, `year-month-reopen-active`)
| Споживач | Рішення |
|---|---|
| `pages/DocumentPage.tsx` — `issuePanels.map(CalculationIssuesPanel)` | замінено однією `CalculationIssuesStrip` (панелі — у її «Technical details») |
| `pages/DocumentPage.tsx` — `YearReopenedBanner` (L7c) | замінено `ReopenedMonthsChip` у шапці; компонент і його a11y-тест видалено |
| `periodReopen/YearMonthReopen.tsx` — рядок «Reopened for this document until …» | прибрано; уже відкриті місяці в меню недоступні (`reopened` з картки) |
| `methodologies/CalculationResultsPanel.tsx` → `UnmatchedRowsBannerSelector` | селектор веде спершу на смугу (`document-calc-strip`), фокус — на її Details |
| vitest: `DocumentPage.staleCalculations`, `DocumentPage.yearCalcIssues`, `DocumentPage.yearLayoutL7` | оновлено: розгортають смугу (Details → Technical details) / перевіряють чип |
| e2e: `calculationIssues`, `calculationMissingInputs`, `formulaErrors`, `missingInputMonthAlias`, `sourceChangedAfterApproval` | оновлено: `revealCalcPanels(page)` перед панеллю місяця |
| копія на місяць: банер `useDocumentPeriodReopen` («Reopened for this document until …» з причиною і автором) | без змін (одна згадка свого місяця, не список) |

## 4. Звіт виконавця
- Гілка `lane/integ-doc-notices-compact` — **від lane/integ-documents-board-yearly 411946fb9** (обидві lane правлять `pages/DocumentPage.tsx`); власні коміти: 2c4044727 `[CODE]`, f6ae371a2 `[TEST]` e2e, faada8917 `[TEST]` знімки вкладенням, 9775b8932 `[FIX]` кнопки матриці size=xs (сторож buttonScale; CI 45654492435 був червоний лише на ньому).
- CI lane: run 45662236214 на 9775b8932 — success (повний набір server-part через сід). Evidence 45662235854 — success.
- e2e-stand (workflow_dispatch на faada8917, grep `docNoticesCompact|calculationIssues|calculationMissingInputs|formulaErrors|missingInputMonthAlias|sourceChangedAfterApproval`): run 45654623847 — 7 passed (знімки `docNoticesCompact-*-1440` у артефакті `e2e-stand-report-shard-1`). Фікс 9775b8932 — лише розмір кнопки, e2e не повторював (правило 9).
- Сід: нова секція `seed/features/COLL-doc-notices-compact.sql` (лише нові ключі `document.notices.*`, en/ru/kz) — змінених текстів наявних ключів немає (SeedTextUpdate не зачеплено). Ecr.Architecture.Tests: 1482/1482 після фіксу (перший прогін піймав `document.calcIssues.months.many` від `formatCount` — замінено літералами one/few/other).
- Локально: `tsc -b` 0, `npm run build` ок, eslint ок, vitest `pages/__tests__/DocumentPage*`, `DocumentsPage*`, `features/documents`, `methodologies`, `calcResults` — 195 файлів / 1076 тестів зелені.
- Червоний доказ: нові тести (`CalculationIssuesStrip`, `ReopenedMonthsChip`) на базі не мають модулів — червоні; L7c на базі червоний (чипа немає, є банер-список).
- **AC-5 — знахідки (без правки):** банер «This sheet was updated by another user» = `liveRefresh.updatedNoTime`, тобто сервер (`GetSheetChangesHandler`) знайшов рядки аркуша з новою `RowVersion` після маркера, для яких у вікні журналу `aud.CellChange` (маркер − 15 хв) НЕМАЄ жодного запису, крім `CalcError`/`CalcErrorResolved` (їх `SheetChangeStore.AttributeSql` відкидає) → «чужий, автор невідомий, часу немає». Власний PATCH відсіюється за `ChangedByUserId`, записи перерахунку (`Origin=Recalculation`) — теж (вони «перераховані», не чужі). Отже сам перерахунок значень банера не дає; кандидати, які дають: (1) запис рядка без журналу значень — вставка/видалення рядків (`RowStore`), «дотик» рядків (`NormalizedCellStore.TouchGroupAsync`/`RowStore.TouchRowsAsync`, `UPDATE doc.TableRow SET ModifiedAt`), позначки сиріт (`OrphanScanner`); (2) журнал перерахунку з `ChangedAt` старшим за вікно 15 хв (довгий прогін, `now` узято на старті, коміт пізніше) — тоді навіть рядки перерахунку «без журналу». Що саме писало о 16:44 — встановлюється лише запитом до live-бази (агенту заборонено): `SELECT c.ChangedAt, c.ChangedByUserId, c.Origin, c.TableRowId FROM aud.CellChange c WHERE c.DocumentId = 96 AND c.ChangedAt BETWEEN '<16:25Z>' AND '<16:50Z>' ORDER BY c.ChangedAt;` і порівняти з `doc.TableRow.ModifiedAt` рядків аркуша за той самий інтервал. Правка — сервер (авторство «системний запис від дії цього користувача» або вікно за `CorrelationId`), не клієнт → за брифом зупинка; у цій lane не правлено.
- Відкрите: [question] банер місяця копії на місяць лишився банером (причина/хто відкрив) — згорнути й його в чип? [debt] AC-0 на live doc 96 — після вікна.
Координатор 25.11: прийнято (CI 45662236214 success на 9775b8932; e2e 45654623847 7/7). AC-5 (банер «updated by another user») — серверна причина, винесено в картку integ-stale-banner-audit.

Координатор 25.11 23:1x live b14, doc 96, 1440×900: над сіткою одна смуга 32 px «Calculation: rows not included in 11 months — 45 method step failed · 72 missing input · 3 inactive ▾ Details»; Details → матриця причина × місяць (Jan…Nov, Show rows); перший рядок сітки видно без прокрутки. Чип «Reopened» не показано — і не має: усі перевідкриття doc 96 Expired 25.11 21:00 (doc.DocumentPeriodReopen) — AC-1 на live не спостерігався, доказ — vitest/e2e. AC-0 PASS → live-verified. Знахідка: StepError #ARG січ–сер → integ-land-stationary-arg.
