# RC-NEXT: PATCH комірок - 503 databaseBusy (RC19 P3-2), кореневий дослід ВІДКРИТИЙ

Статус: **P3-2 НЕ закрито.** У lane влито лише спостережуваність: тимчасовий збій БД логується Warning (не Error
«Необроблений виняток») і рахується метрикою `ecr.db.busy.count` (тег `kind`: `timeout` / `deadlock` / `lockTimeout` /
`pool` / `other`). Причина тайм-ауту в `NormalizedCellStore.UpsertAsync` не встановлена.

## Що відомо

- Інцидент (LAND-RC19): один `503 databaseBusy` на `PATCH /documents/{id}/cells` (100 рядків), `Execution Timeout Expired`
  у `NormalizedCellStore.UpsertAsync`; чанк тривав 278 с проти ~110 с у решти. Одиничний, стенд знищено.
- `-2` - клієнтський тайм-аут команди, а не 1222: `SET LOCK_TIMEOUT 15 с` (`LockWaitGuard`) спрацював би раніше на
  ЧЕКАННІ ЗАМКА. Отже команда або виконувалась довго, або чекала не на замку (журнал, tempdb/version store, пам'ять,
  автоприріст файлів, CPU harness).
- Сирі команди запису комірок (`connection.CreateCommand()` у `NormalizedCellStore`) не виставляють `CommandTimeout`,
  тому діє default SqlClient 30 с, а не 60 с з `Database:CommandTimeoutSeconds` (той діє лише на команди EF).
- `MERGE doc.CellValue WITH (HOLDLOCK)` вже один set-based запит з TVP; TVP має PK `(PeriodKey, TableRowId,
  ColumnDefId)`, тому сортування рядків перед відправкою нічого не змінює (спробу `InKeyOrder` відхилено як no-op).

## Відтворення (для наступного досліду)

1. Стенд з даними ~2 млн значень у `doc.CellValue`, RCSI увімкнено, файли даних з малим автоприростом.
2. Два паралельні `PATCH /api/v1/documents/{id}/cells` по 100 рядків із ПЕРЕТИНОМ комірок (ті самі `tableRowId` ×
   `columnDefId`), клієнти шлють їх у протилежному порядку; сотні ітерацій на барʼєрі.
3. Паралельно - важке фонове навантаження (перерахунок/перенос версії), як у харнесі `q15-full.js`.
4. Під час кожного запиту знімати `sys.dm_exec_requests` (`wait_type`, `wait_time`, `blocking_session_id`),
   `sys.dm_os_wait_stats` (дельта) і події autogrowth (`default trace`); фіксувати `ecr.db.busy.count{kind}`.

## Що вирішити за результатом досліду

- Якщо причина - автоприріст/ресурси: параметри бази стенду, не код.
- Якщо довге очікування НЕ на замку, а саме виконання MERGE: показати план (actual) і статистику TVP/секцій.
- Якщо потрібне вирівнювання `CommandTimeout` сирих команд до конфігу - окреме рішення (відсуває, а не усуває 503).
