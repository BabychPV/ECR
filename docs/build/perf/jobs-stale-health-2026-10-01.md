# Продуктивність: статус задачі, заст. зрізи, /health/ready (2026-10-01)

Enterprise-прохід №2 (`3-performance.md`, P1 №1–3) був СТАТИЧНИМ: плани не міряли. Тут — вимір і виправлення.

## Методика

База `Ecr_PerfFix` (повне розгортання `tools/setup-dev-db.ps1 -Documents 0`), наповнення T-SQL:
`itg.JobProgress` — 500 000 рядків `Succeeded` за 30 діб (половина — дочірні задачі розкладу з
`Payload` ~350 байт і `fanOutParentJobId`, 2000 різних батьків; кожен 50-й — з ключем
`job.recalc.overBudget` у `Message`); `calc.CalculationRun` — 1 500 000 прогонів (100 проєктів,
300 `Current`, решта `Superseded`); `rpt.ReportSnapshot` — 4 000 зрізів. Вимір:
`SET STATISTICS IO/TIME ON`, запити — у формі, яку генерує код (`sp_executesql` з параметрами).
Після виміру базу видалено.

## Результати

| # | Запит | До (читань / час) | Після | Виправлення |
|---|---|---|---|---|
| P1-1 | `GetFanOutAsync` (статус проєктної задачі `Running`/`Succeeded`) | 47 116 / 2,5 с (CPU 3,8 с) | 4 / 14 мс | persisted-стовпець `FanOutParentJobId` + індекс `IX_JobProgress_FanOutParent (FanOutParentJobId) INCLUDE (State)`; запит за стовпцем; документна задача (DocumentId > 0) дітей не має — виклик пропущено |
| P1-2 | перевірка заст. зрізів при завершенні прогону (`ReportSnapshotStaleness`) | 21 231 / 18,4 с | 120 / 5 мс | `IX_CalculationRun_Project_FinishedAt (ProjectId, FinishedAt) INCLUDE (PeriodKey, Status, ErrorMessage)`; `FK_CR_Project` індексу не мав |
| P1-3 | `CountSucceededWithMessageKeyAsync` (`/health/ready`, анонімний) | 47 236 / 1,6 с | 254 / 0,19 с | `IX_JobProgress_State_UpdatedAt (State, UpdatedAt) INCLUDE (Message)` |

## Що пробували й відкинули

- Звуження дітей розкладу за `UpdatedAt >= parent.StartedAt` на індексі `(State, UpdatedAt)`: на
  параметризованому запиті 8 237 читань за годину й 74 573 за добу (key lookup на Payload) — гірше за скан.
- Фільтрований індекс по `FanOutParentJobId IS NOT NULL`: параметризований запит його не бачить (скан,
  51 970 читань). Індекс навмисно нефільтрований.
- Індекс `(State, UpdatedAt)` без INCLUDE (Message): 49 044 читань — оптимізатор не ходить у key lookup.
- Кеш `/health/ready`: не потрібен — 254 читання на пробу; плата — одна ширша структура в гарячій таблиці.

## Ціна

- `itg.JobProgress` отримує два індекси (запис `Message`/`State` оновлює їх): рядки черги оновлюються
  на старті, прогресі й завершенні. Обсяг — одиниці сотень байт на рядок.
- Міграція `PerfFixJobsStaleHealth` додає persisted-стовпець: на оновленні — перерахунок по всіх рядках
  журналу (на 500 тис. рядків стенда ~20 с).

## Сторожі

`JobProgressHotQueriesScanTests` (Infrastructure, 2 тести) і
`ReportSnapshotStalenessTests.Перевірка_зрізів_не_читає_давніх_прогонів_проєкту`: читання
`sys.dm_exec_sessions.logical_reads` не ростуть після 40 000 чужих рядків. Мутація: прибрати
`CreateIndex` із міграції — статус розкладу 258 → 1 936, зрізи 33 → 499, лічильник готовності
199 → 545 читань, тести червоні.
