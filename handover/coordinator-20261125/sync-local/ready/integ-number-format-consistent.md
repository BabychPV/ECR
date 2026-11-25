# Картка: number-format-consistent Один формат чисел у таблицях і полях
Статус: live-verified
Гілка: lane/integ-number-format-consistent
База: d75032896 (origin/lane/integ-b13)
Пачка: b14 (SHA в AC — cherry-pick пачки lane/integ-b14 → live 7afb19c97)
Ризик: R1
Рішення: знахідка UI-проходу live b13 (25.11, координатор) до number-display (людина 24.11 «інтуітивно зрозуміло і охайно»)
Дизайн: не потрібен

## 1. Що і навіщо
- Permits → дозвіл KZ06VCZ14825472 → Emission limits: в одному стовпці «75,435.96» (з розділювачем тисяч, бо скорочено; повне значення в підказці) поруч із «9363.267» (без розділювача); «8.7E-7» — експоненційний запис.
- Gas composition → версія: поля «1.397E-9», «4.013E-13» поруч із «0.00004283738».

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ: тест / команда / live | SHA |
|----|-------------------------------------|------------------------------|-----|
| AC-0 | live: Emission limits KZ06VCZ14825472 і склад газу — усі числа одного вигляду (розділювач тисяч скрізь або ніде; без «E-»), повне значення в підказці; значення в БД не змінюються | live скрин — за координатором після вікна; на CI-стенді: e2e permitNumberFit (стовпець mg/nm3 «9,363.267 / 75,435.96 / 162,121.138», підказка «75,435.9612345», жодної «E») і gasComposition | e40b9628a, 8f2a2b74e, 48d0c1684 |
| AC-1 | спільне форматування: малі числа без експоненти (до точності сховища), однакове групування незалежно від того, скорочено число чи ні | vitest `shared/format/__tests__/numberFormatConsistent.test.ts` (13), `PermitDefTable.test.tsx` «групування одне на стовпець», `airCardModel.test.ts` «групування стовпця» — червоні на d75032896, зелені на e40b9628a | e40b9628a |
| AC-2 | поле вводу показує число без експоненти; введення «1e-9» приймається і зберігається те саме значення | vitest `DecimalInput.test.tsx` (AC-2 ×2), `VersionEditorModal.test.tsx` (AC-2: payload `0.000000001`), `airCardModel.test.ts` (поле правки з точного значення); e2e `gasComposition.spec.ts` (ввід «1e-9» → «0.000000001», копія збереженої версії показує те саме) | e40b9628a, 8f2a2b74e, 48d0c1684 |

## 3. Споживачі спільного форматування (аудит 25.11: grep toExponential / toPrecision / toLocaleString / Intl.NumberFormat / formatDecimal* / DecimalInput / DecimalText)
Правило групування (вибір, записано в коміті e40b9628a): **таблиця — на стовпець** (`columnGrouping`): є хоч одне значення стовпця (або його підсумок) з цілою частиною ≥ 10 000 → розділювач у ВСІХ значеннях від 1 000; інакше ніде (рік/код «2031» без коми). **Поля вводу — ніколи.** Окреме значення поза стовпцем — як було (від 10 000). Експоненти в поданні немає ніде.

| Споживач | Було | Рішення |
|---|---|---|
| `shared/format/number.ts` `formatDecimalReadable` | науковий запис < 1e-6, групування min2 на значення | змінено: без експоненти (7 значущих після провідних нулів, до точності сховища), параметр `grouping`; нові `columnGrouping`, `plainDecimal`, `expandExponentInput` |
| `shared/format/number.ts` `formatDecimal` (точний) | число в експоненті (`1e-7`) → `null` → викликач показував «1e-7» | змінено: розгортається зсувом коми (CalcResultsGrid, DataTable, units, audit — без «E») |
| `shared/ui/DecimalInput.tsx` (дозволи RowFields, Air AirRowEditor, склад газу) | науковий запис, групування min2 | змінено: `never`; експонента на blur → той самий десятковий |
| `shared/ui/DecimalText.tsx` | min2 | змінено: проп `grouping`; склад газу «Попередня» — `never` |
| `features/permits/table/PermitDefTable.tsx` (Emission limits тощо) | групування на значення | змінено: на стовпець (комірки, згорнуті групи, підсумки, підказка) |
| `features/permits/air/AirLimitsTab.tsx` + `airCardModel.ts` (джерела, речовини, підсумки, додані колонки) | на значення; поле правки починалось з округленого «75,435.96» | змінено: на стовпець; `initialValues` — точний `plainDecimal` |
| `features/gasComposition/VersionEditorModal.tsx` | `parse` відхиляв «1e-9» | змінено: `parse` через `expandExponentInput` |
| Сітка документа `features/grid/cellValue.ts` (`formatDecimal` зі scale колонки) | десяткові рядком без експоненти; групування Intl однакове на весь стовпець | **не змінено**: точність сховища й доповнення scale не чіпаються; експонент у сітці немає (Int-числа < 1e21, а `formatDecimal` тепер і їх розгортає) |
| `shared/ui/DataTable` `formatNumber`/`formatDecimal` | Intl standard — без експоненти, групування завжди | не змінено (уже однаково) |
| `features/gasComposition/StreamDetailBody.tsx` `sumText` (Σ ≈ 100) | `String(round(x,6))` | не змінено: Σ не буває < 1e-6 |
| `shared/formula/evaluate.ts` `formatNumber` (`toPrecision(15)`) | підказка формули «як сервер» | не змінено: не подання збережених значень |
| `features/piData/SeriesChart.tsx` (`maximumSignificantDigits: 6`) | Intl standard — без експоненти | не змінено |
| лічильники `formatNumber(count)` (~80 місць) | цілі | не змінено |

## 4. Звіт
- Гілка `lane/integ-number-format-consistent` від d75032896: **e40b9628a** [CODE] (формат + споживачі + vitest), **8f2a2b74e** [TEST] (e2e permitNumberFit, gasComposition), **48d0c1684** [TEST] (виправлення очікувань e2e: підпис поля меркаптану; «162,121.1» при 7 значущих). Вершина lane — 48d0c1684.
- Червоне до фіксу: вихідники d75032896 + нові тести → 22 червоні у 5 файлах; після — зелені.
- Локально: vitest shared/permits/gasComposition/grid/calcResults/audit/units/pages/admin — 665 файлів, 4884 тести зелені; `tsc -b` 0; eslint 0; `npm run build` 0.
- CI (push, lane/integ-* — повний набір) на 48d0c1684: run 45647003817 — **success**; Evidence 45647003426 — **success**. (CI 45643584863 на 8f2a2b74e скасовано новим пушем.)
- e2e-stand (workflow_dispatch, grep `permitNumberFit|gasComposition|permitPage|airLimitsReal|airParams|airRealShape|airLastTable|registryTableHeader`): run 45644059734 на 8f2a2b74e — 41/43 зелені, 2 червоні були хибними очікуваннями нових e2e (виправлено 48d0c1684); повтор grep `permitNumberFit|gasComposition` на 48d0c1684: run 45647000402 — **success**. (45643710841 впав на checkout: короткий SHA у `ref`, не тест.)
- БД не змінюється: лише подання й розбір вводу; незмінене поле не надсилається.
- Знімки до/після на власному стенді не робились (споживача немає: e2e на CI зберігає permit-number-fit-1440.png і gas-composition-*.png в артефактах); AC-0 live — за координатором.
- Відкрите: окреме число поза стовпцем лишилось «від 10 000» (DecimalText в OverviewTab — одне значення); якщо треба «завжди від 1 000» і там — одна зміна дефолту `grouping`.

Координатор 25.11: прийнято (CI 45647003817 success, e2e grep 45647000402 success на 48d0c1684; diff 15 файлів, shared/ui → повний e2e на пачці b14). Окреме число поза стовпцем — групування від 10 000 лишаємо (рік/код не групується).

Координатор 25.11 23:1x live b14: Permits → KZ06VCZ14825472 → Emission limits — стовпець mg/nm3 «9,363.267 / 75,435.96 / 162,121.1 / 3,805.42», малі «0.00000087», жодної «E»; Gas composition (перелік) — 0 «E». Поля редактора версії складу газу на live не відкривав (доказ — e2e gasComposition). AC-0 PASS → live-verified.
