# Лист готовності: lane/integ-methodology2 (похідні аргументи + перелік категорій констант)

- Гілка / lane: `lane/integ-methodology2` (замінює `lane/integ-methodology` @ 704c2f732 — та гілка застаріла, CI скасовано; її можна видалити)
- Пункт: похідні аргументи (`@Name?`) + CONST-CATEGORY-PLAN (сервер і контракт; клієнт — Permites)
- Хеш tip: `d7728263eb61dc8b4a4194da17556efca77c653f` (контракт у цьому ж коміті)
- База: `origin/main` `c259627bf81952d899722c34db0cff68893b8a40` (після #668 RPTV01 і #669)
- Лінійна: 1 коміт, merge-комітів 0; мутацій у кінчику немає
- Sql: так — `09-seed.sql` (секції `METH:categories` en у MERGE + ru/kz у `#I18N`, лише додавання, 32 рядки)
- EF-міграція: так — `20261102000813_METH01_MethodologyCategory`, батько `20261101211255_RPTV01_ProjectVersionTimeline`; знімок перегенеровано на c259627bf. Таблиця `calc.MethodologyCategory` + сід переліку з наявних категорій констант (повторюваний, `NOT EXISTS`)
- Дані: крок старту `MethodologyArgumentsBackfill` (C#, після сіду; ідемпотентний — лише версії старого формату; звіт у журнал старту)
- Контракт openapi.snapshot.json / schema.d.ts: так, перегенеровано ОСТАННІМ; повторна генерація побайтно та сама (хеш до/після однаковий); `npm run typecheck` чистий
- shared/ui, package*.json, lock, CLAUDE.md, .github: ні
- Нові ключі UiString (en/ru/kz): `err.ECR-CALC-0404.category`, `err.ECR-CALC-0409.categoryInUse`, `err.ECR-CALC-0409.categoryRename`, `err.ECR-CALC-0422.categoryCodeInvalid`, `err.ECR-CALC-0422.constantCategoryUnknown`, `err.ECR-CALC-0422.constantCategoryRetired`, `publish.warning.categoryRuleLiteralNoCategory`
- Нові коди помилок: ні (ключі на наявних ECR-CALC-0404/0409/0422; 02-contracts §7.1)
- Нові маршрути: `GET/PUT/DELETE …/methodologies/{id}/versions/{vid}/categories[/{code}]` (View / EditConstant); EndpointCoverage — тимчасове звільнення до клієнтської lane
- Поле запиту формули `argumentsCsv` — застаріле, приймається 1 реліз (лише маркери `Name?` переходять у вираз)

## CI
- CI (повний, push lane/integ-*): run 41366775144 — success (server-part ×9, client, a11y dark/light, windows, msi, msi-install)
- Evidence: run 41366775161 — success

## Локально
- build ✓; Architecture 1170/1170 ✓; Expressions 39 ✓; Application (Calculations) 481 ✓; Calculations 453 ✓; Infrastructure (Methodology) 28 ✓; Api (категорії/область/речовини) 21 ✓; раніше Api методологій 152/152
- Мутації: маркер лише з власного списку → червоні 2 тести нормалізації + 4 еквівалентності; без `RequireUnused` у видаленні категорії → червоний 409

## Доказ на копії Land (НЕ EcrLandUi)
- Копія: `EcrMethScratch` з `F:\ecr-backup\land-ui\k1-window14w\bak\EcrLandUi-after-W14w-20261102-0208.bak` (дані E:\EcrData\methscratch, журнал C:\EcrScratch\meth); вхід — тестовий обліковий запис лише в копії; вихідні канали в копії порожні (SmtpSettings/NotificationChannel/DataSource = 0, outbox очищено)
- ДО: збірка main c259627bf (H:\ecr-wt\meth-main), `POST /documents/{id}/recalculate` для всіх 419 пар документ×період, що мали результати → 419/419 Succeeded
- Міграція METH01 ідемпотентним скриптом двічі (`sqlcmd -I -b`): 830 категорій у 59 версіях, другий прохід — 0 змін
- ПІСЛЯ: збірка lane d7728263e (старт виконав крок вирівнювання), ті самі 419 пар → 419/419 Succeeded
- Порівняння (поточні рядки кожного проходу, без Id/RunId):
  - `calc.CalculationResult` (PeriodKey, MethodologyVersionId, DocumentId, SourceRowKey, OutputCode, Value, UnitId, SubstanceEntryId, Kind, TableInstanceId): до 554 554, після 554 554; before EXCEPT after = **0**, after EXCEPT before = **0**
  - `doc.CellValue WHERE IsCalculated = 1` (усі колонки значення): до 4 672 254, після 4 672 254; EXCEPT обидва боки = **0**
  - Запит (скорочено): `SELECT COUNT(*) FROM (SELECT * FROM dbo.meth_res_before EXCEPT SELECT * FROM dbo.meth_res_after) x` і навпаки; те саме для `meth_cell_*`; «після» = рядки прогонів з `calc.CalculationRun.StartedAt >= 2026-11-02T00:55:58Z`
- Ефект вирівнювання на Land: версій 59, формул переписано 565 (ArgumentsCsv → похідний формат), вирази змінено в 40 формулах (+40 маркерів `?`: ECW_C11_03 8, ECW_C11_10 V1 6 / V2 6, ECW_C11_11_03 2, ECW_C11_16 2, ECW_C12_02 V1 16), маркерів зникло 0, невжитих елементів списків прибрано 30 у 7 формулах:
  1. ECW_C05_04/V1 ECW_EC_tons_330: `(CST.k5_ECW_C05_04_` (описка в списку)
  2. ECW_C12_02/V1 ECW_EC_gsec_415_type1: `@Density0`, `@Land_Measure_MaximumConsumption`, `@Vddg`, `CST.C1C5_ECW_C12_02_`, `CST.k4_ECW_C12_02_`, `CST.kp1_ECW_C12_02_`
  3. ECW_C12_02/V1 ECW_EC_tons_415_type1: `@Density0`, `@Land_Measure_GasConsumption`, `@Vddg`, `CST.C1C5_ECW_C12_02_`, `CST.k4_ECW_C12_02_`, `CST.kp_ECW_C12_02_`
  4. ECW_C11_13/V2 ECW_EC_tons_2735_type2: `CST.Q_ECW_C11_13_`, `@Land_Measure_OperatingTime`, `CST.n_ECW_C11_13_`, `CST.k2_ECW_C11_13_`, `CST.MF_ECW_C11_13_`
  5. ECW_C11_13/V2 ECW_EC_tons_2754_type1: `CST.Q_ECW_C11_13_`, `@Land_Measure_OperatingTime`, `CST.n_ECW_C11_13_`, `CST.Kp_ECW_C11_13_`, `CST.MF_ECW_C11_13_`
  6. ECW_C11_13/V2 ECW_EC_tons_333_type1: ті самі 5, що в п.5
  7. ECW_C12_01/test ECW_EC_tons_XXX_type1: `@NoSafValves`, `@_@No_Valves` (тестова чернетка)

## Ризики / обмеження
- Маркер у бібліотеці, чия потрібна формула ім'я не вживає, для викликача зник би (на Land маркерів у Common немає — 0 зниклих).
- `@A?1:2` (тернарний без пробілу) тепер маркер + відмова розбору; у корпусі AF/Land таких записів немає.
- Публікація не відмовляє за категорію константи поза переліком (у плані — помилка (a)); введення через API це вже не пропускає.

## Хто зводить
- Координатор (RC з SHA d7728263e; повний CI уже зелений на цьому SHA)
