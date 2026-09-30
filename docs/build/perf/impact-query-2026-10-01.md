# Запит виміру впливу довідника: замір і індекс (2026-10-01)

`GET /api/v1/registries/{code}/impact` (`RegistryImpactStore.ListImpactedAsync`) іде від версій
методологій, що читають довідник (`cfg.RegistryUse`, `SourceKind = 1`), до `calc.CalculationResult`
за `MethodologyVersionId` — без фільтра по періоду.

## Було

Індекси `calc.CalculationResult`: `PK (Id, PeriodKey)`, `IX_CalculationResult_Lookup (PeriodKey,
DocumentId, MethodologyVersionId, OutputCode)`, `IX_CalculationResult_UnitId`. Жоден не починається з
`MethodologyVersionId`, таблиця партиційована за `PeriodKey` → запит скануює всі партиції.
`cfg.RegistryUse` має `IX_RegistryUse_Registry` та `IX_RegistryUse_Source` — достатньо (3–4 читання).

## Замір

Окрема база (`Ecr_ImpactMeasure`, видалена після заміру), повне розгортання (міграції + `Sql/*.sql`),
дані згенеровано T-SQL (FK вимкнено на час вставки): 500 000 `CalculationResult` (141 МБ), 24 000
`CalculationRun` (60 % Current, 25 % Superseded, 15 % Failed), 2 000 документів у 20 проєктах,
240 періодів (Closed/Grace/Open), 80 версій методологій, 10 довідників, 360 ребер `RegistryUse`.
SQL відтворює запит EF вручну (ті самі join/фільтри/DISTINCT/TOP), `STATISTICS IO/TIME`, гарячий кеш.

| довідник | версій читає | до: logical reads (CalculationResult) | після | до: CPU/elapsed | після |
|---|---|---|---|---|---|
| R1 (3 версії з 80) | 3 | 8 430 (scan count 25) | 108 | 219/274 мс | 31/38 мс |
| R5 (10 версій з 80) | 10 | 8 424 | 1 048 | 265/306 мс | 94/109 мс |

До: читання сталі (≈ вся таблиця, не залежать від довідника) і ростуть лінійно з таблицею. Після:
seek по `MethodologyVersionId` у кожній партиції, читання пропорційні кількості ЗАЧЕПЛЕНИХ рядків.
Бойовий обсяг (~рік даних, десятки/сотні мільйонів рядків) без індексу дав би ×100–×1000 від 8 430.

## Висновок: індекс потрібен і доданий

`IX_CalculationResult_Version ON calc.CalculationResult (MethodologyVersionId, PeriodKey)
INCLUDE (CalculationRunId, DocumentId) ON ps_ByPeriodKey (PeriodKey)` — покривний для цього запиту.

- Живе в `Sql/07-partition-tables.sql` (не міграція EF: потрібне розміщення на схемі партиціонування),
  ідемпотентно (`IF NOT EXISTS`), до фінальної перевірки 50031 «індекс поза схемою».
- ONLINE не використано (Standard). На заповненій базі (`-Upgrade`) побудова офлайн — вікно
  обслуговування; ціна — ще одна копія 3 колонок + ключ на кожен результат (~25–30 % від розміру
  `IX_CalculationResult_Lookup`), запис результатів дещо дорожчий.
- Сторож: `RegistryImpactScanTests` — читання доступу «версія → результати» не ростуть від 40 000
  чужих рядків (мутація: ключ індексу `(OutputCode, PeriodKey)` → 43→511 читань, тест червоний).

⚠ Обмеження заміру: на малій базі оптимізатор може вести повний запит від `doc.Document`
(seek по `IX_CalculationResult_Lookup` з трьома рівностями), тому сторож міряє саме шлях доступу за
версією, а не весь запит. На бойових обсягах повний запит іде через новий індекс (як у замірі вище).
