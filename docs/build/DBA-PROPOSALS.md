# Пропозиції для DBA — з вимірами (2026-10-08)

Єдине місце для пропозицій, що потребують DDL або рішення DBA. Рядки 2 і 4 мають статус **proposed**: у `Sql/` і в міграції EF для них нічого не додано; рядок 1 заміщено індексом розгортання (PSS-14); рядок 3 виконано (AN-112, дозвіл людини 09.10) (D-66 — застосунок без DDL; D-264 — DBA на замовнику). Скрипти нижче — **приклади**, не виконувались на прод-базі. Задачі в черзі — `docs/build/WORK-QUEUE.md`, `DB-5`, `DB-6`. Походження: `docs/build/TODO-REMAINING.md` B3.6/B3.7.

| # | Пропозиція | Хто вирішує | Статус |
|---|---|---|---|
| 1 | Індекс `IX_CellChange_Doc_Period_ChangedAt` на `aud.CellChange` | замовник/DBA (К10) | **заміщено** (`IX_CellChange_DocPeriodChanged`, CL-10b; PSS-14) |
| 2 | `LOCK_ESCALATION` на `doc.CellValue` / `doc.TableRow` | DBA (замовника не питаємо — рішення координатора 08.10) | proposed |
| 3 | Фільтрований індекс `IX_CellChange_LateEdit` на `aud.CellChange` | людина (09.10, HU-12/HU-13, `D-343`) | **виконано** (AN-112) |
| 4 | Стиснення `PAGE` некластерних індексів (`calc.*`, `aud.*`, `doc.*`) | DBA (PSS-14) | proposed |

## 1. Індекс `aud.CellChange` для resultsStale (DB-5)

> ✎ 2026-11-20 (аудит PSS-14): **заміщено, DBA нічого не робить.** Той самий ключ
> `(DocumentId, PeriodKey, ChangedAt)` з `INCLUDE (Origin, ChangedByUserId)` на `ps_AuditByMonth(ChangedAt)` уже
> створює розгортання — індекс `IX_CellChange_DocPeriodChanged` (`Sql/11-audit-tables.sql`, CL-10b, разом із
> дзеркалом у `Sql/12-archive-tables.sql` для SWITCH архіву). Скрипт нижче створив би другий, майже ідентичний індекс
> (ще один запис на кожну правку комірки) — **не виконувати**. Різниця лише в `INCLUDE (ColumnDefId)`, якого запити
> resultsStale не читають. Таблицю нижче лишено як історію замірів.

| Поле | Зміст |
|---|---|
| Навіщо | Лічильники «результати застаріли» (`staleResultsCount`, `/documents/summary`) і фільтр `GET /documents?resultsStale=true` ходять по аудиту комірок (`aud.CellChange`) |
| Виміри | 2000 документів / 504 тис. рядків `aud.CellChange`, усі застарілі (гірший випадок). Фільтр/лічильник resultsStale по проєкту: ≈1,0 с -> ≈0,35 с. Лічильник «мої»: 0,4-0,6 с -> ≈0,10 с. Позначка на сторінку документів: 16-26 мс -> 10-13 мс. На 10x обсязі без індексу ≈10 с (екстраполяція, не вимір) |
| Ціна | Ще один індекс на найбільшій таблиці аудиту; додатковий запис на кожну правку комірки; DDL на партиціонованій таблиці (вікно на побудову індексу залежить від обсягу) |
| Ризик | Низький для даних (лише індекс). Побудова на великій таблиці тримає ресурси; рекомендується поза піком. Скрипт ідемпотентний |
| Поточне пом'якшення | Кеш лічильників, TTL 45 с (`DocumentListSummaryStore.StaleCountsTtl`); `GET /documents?resultsStale=true` не кешується, rate limit для нього немає (борг B3.7) |
| Хто вирішує | Замовник/DBA (К10) |
| Статус | ~~proposed~~ **заміщено** `IX_CellChange_DocPeriodChanged` (2026-11-20, PSS-14) |

~~Приклад скрипта~~ (історія — не виконувати; потрібен був би `sqlcmd -I`, бо індекс вимагає `QUOTED_IDENTIFIER ON`):

```sql
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_CellChange_Doc_Period_ChangedAt'
                 AND object_id = OBJECT_ID(N'aud.CellChange'))
    CREATE NONCLUSTERED INDEX IX_CellChange_Doc_Period_ChangedAt
        ON aud.CellChange (DocumentId, PeriodKey, ChangedAt)
        INCLUDE (Origin, ColumnDefId, ChangedByUserId)
        ON ps_AuditByMonth(ChangedAt);
```

## 2. LOCK_ESCALATION на `doc.CellValue` / `doc.TableRow` (DB-6)

| Поле | Зміст |
|---|---|
| Навіщо | `migrate-version` на проєкті з ≈2,06 млн значень триває ≈14 хв (пачки по 5000 в одній транзакції) і тримає блокування до коміту; ескалація до таблиці блокує PATCH комірок в інших проєктах |
| Виміри | Раніше: PATCH комірки в інший проєкт через ≈30 с давав 500. Тепер (lane `fix-patch-lock-timeout`, влито в AN-106): 409 `ECR-DOC-4091` lockTimeout за ≈15 с (`LOCK_TIMEOUT 15000`, `LockWaitGuard`) — і на значеннях, і на нових рядках, аудиті `aud.CellChange` та «дотику» документа. Для Land (138 тис. значень, 25-60 с) вікно мале. З `DISABLE` очікування: перенос блокує лише свої рядки, PATCH в інші проєкти не чекає взагалі (очікування, не вимір) |
| Ціна | Більше пам'яті під блокування (по рядку замість ескалації) на час переносу; потрібен DDL/міграція і дозвіл DBA |
| Ризик | Середній: на великих переносах зростає кількість блокувань у пам'яті сервера. На партиціонованих таблицях можна розглянути `AUTO` (ескалація до партиції) |
| Альтернатива | Окремі транзакції по таблицях у `MigrationStore` — порушує атомарність переносу, **не рекомендується** |
| Хто вирішує | DBA; замовнику не питаємо (рішення координатора 08.10) |
| Статус | proposed |
| Уточнення (аудит PSS-2026-11-20, PSS-05) | Ескалація на стенді **не спостерігалась** (DMV з 17.11: `doc.CellValue` 0 спроб / 0 промоцій, `aud.CellChange` 7 / 0) — пропозиція превентивна. Застосунок сам обмежує оператор запису ≤ 4000 рядків у тій самій транзакції (`NormalizedCellStore.CellWriteChunkSize`: `MERGE doc.CellValue`, `INSERT aud.CellChange`; тест `CellBatchEscalationTests`), тож типові пакети поза порогом ≈5000 замків. `LOCK_ESCALATION = AUTO` **не** рекомендується як заміна: уся конкуренція — в одній партиції (період), ескалація до партиції блокує ті самі записи |

Приклад скрипта:

```sql
ALTER TABLE doc.CellValue SET (LOCK_ESCALATION = DISABLE);
ALTER TABLE doc.TableRow  SET (LOCK_ESCALATION = DISABLE);
-- AUTO (ескалація до партиції) не допомагає: усе змагання в одній партиції, див. «Уточнення» вище
-- перевірка:
SELECT name, lock_escalation_desc FROM sys.tables
WHERE object_id IN (OBJECT_ID(N'doc.CellValue'), OBJECT_ID(N'doc.TableRow'));
```

## 3. Фільтрований індекс пізніх правок `aud.CellChange` (P1-05, AN-109)

| Поле | Зміст |
|---|---|
| Навіщо | Позначка «пізня правка» (`DocumentStore.LateEditDocumentIds`) на кожній сторінці `GET /documents`, на картці документа і фільтр `hasLateEdits`. `IsLateEdit` і `PeriodKey` немає ні в `IX_CellChange_Cell`, ні в DB-5, тож на кожну зміну документів сторінки за всю онлайн-історію (≈24 місяці до архівації) іде key lookup у кластерний індекс; `hasLateEdits` без документа сканує всі партиції |
| Що вже зроблено в коді (AN-109) | Запит розгалужено на два тексти (за період / за будь-який) замість `(@any = 1 OR c.PeriodKey = @p)`: `PeriodKey` став sargable-предикатом під цей індекс. Межі `ChangedAt` у запиті немає свідомо: з даних її не вивести без втрат (`OpenOffsetDays` може бути від'ємним, правка «поза вікном» за Warn-політикою D-239, пізня правка в стані Open після Reopen аркуша D-70 б) |
| Виміри | Не мірялось. DB-5: позначка на сторінку 16-26 мс на 504 тис. рядків; на 10x — екстраполяція 0,2-0,5 с |
| Ціна | Індекс майже порожній (пізні правки рідкісні: лише Grace/Reopen), запис — лише для рядків з `IsLateEdit = 1` |
| Ризик | Низький (лише індекс). Потрібен `QUOTED_IDENTIFIER ON` (`sqlcmd -I`), як для `IX_CellChange_OutOfWindow` |
| Хто вирішує | DBA (К10) |
| Статус | ✎ 2026-10-09: **виконано** (AN-112; дозвіл людини 09.10, `D-343`). Індекс у `Sql/11-audit-tables.sql` і дзеркало в `Sql/12-archive-tables.sql` (`arc.AuditCellChange` — інакше SWITCH архіву падає): ключ `(DocumentId, PeriodKey)`, `INCLUDE (ColumnDefId)`, `WHERE IsLateEdit = 1`, `ON ps_AuditByMonth(ChangedAt)`, ідемпотентно (`IF NOT EXISTS`), `ONLINE = ON` лише на `EngineEdition` 3/5/8 (як `B18HotPathIndexes`). Звірено з предикатом після AN-109: `IsLateEdit = 1` — фільтр, `DocumentId IN (…)` і `PeriodKey = @p` — ключ, `ColumnDefId` (NOT EXISTS схованих колонок) — INCLUDE. Сторож — `AuditLateEditIndexTests` (Architecture). Вимірів після індексу немає |

Приклад скрипта (у стилі `IX_CellChange_OutOfWindow` з `11-audit-tables.sql`):

```sql
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CellChange_LateEdit'
               AND object_id = OBJECT_ID(N'aud.CellChange'))
    CREATE INDEX IX_CellChange_LateEdit
        ON aud.CellChange (DocumentId, PeriodKey)
        INCLUDE (ColumnDefId)
        WHERE IsLateEdit = 1
        ON ps_AuditByMonth(ChangedAt);
```

## 4. Стиснення `PAGE` некластерних індексів (PSS-14)

| Поле | Зміст |
|---|---|
| Навіщо | Розгортання стискає лише кластерні ключі, позначені в карті `Sql/07-partition-tables.sql` (`CompressPk`); некластерні індекси лишаються `NONE`. На стенді Land (`EcrLandMsLegacy3`, 20.11, лише читання DMV): 810 із 810 партицій некластерних індексів без стиснення, разом ≈3,2 ГБ; найбільші — `calc.CalculationResult` `IX_CalculationResult_DocRun` ≈1,1 ГБ, `_Lookup` ≈1,0 ГБ, `_Version` ≈0,4 ГБ, `_UnitId` ≈0,4 ГБ; `aud.CellChange` `IX_CellChange_Cell` і `IX_CellChange_DocPeriodChanged` по ≈70 МБ |
| Виміри | Економію не міряно (на стенді — лише розміри). DBA міряє перед рішенням: `EXEC sp_estimate_data_compression_savings @schema_name = N'calc', @object_name = N'CalculationResult', @index_id = NULL, @partition_number = NULL, @data_compression = N'PAGE';` — і так для `aud.CellChange`, `doc.CellValue`, `doc.TableRow` |
| Ціна | CPU на запис і читання стиснених сторінок; перебудова індексу (`ALTER INDEX … REBUILD PARTITION = ALL WITH (DATA_COMPRESSION = PAGE)`) — вікно, журнал транзакцій, на Standard — офлайн |
| Ризик | Низький для даних. ⛔ Індекси `aud.CellChange`/`aud.StructureChange`/`aud.SecurityEvent`/`aud.PublicationEvent` стискати ЛИШЕ разом із дзеркалами в `arc.Audit*` (`Sql/12-archive-tables.sql`): SWITCH архівації аудиту вимагає однакового стиснення партицій джерела й цілі (інакше Msg 4947 і рядок `audit-archive`/`Failed`) |
| Зв'язок | Стиснення кластерних `calc.*` (PSS-01, лан L3) — окремо, у `07-partition-tables.sql`; цей рядок — лише некластерні індекси, які розгортання не чіпає |
| Хто вирішує | DBA |
| Статус | proposed |

Приклад скрипта (після виміру; поза піком):

```sql
-- calc.*: без дзеркал, по одному індексу
ALTER INDEX IX_CalculationResult_DocRun ON calc.CalculationResult
    REBUILD PARTITION = ALL WITH (DATA_COMPRESSION = PAGE);
-- aud.*: ЛИШЕ парою з дзеркалом arc.Audit* (SWITCH архіву)
ALTER INDEX IX_CellChange_Cell ON aud.CellChange REBUILD PARTITION = ALL WITH (DATA_COMPRESSION = PAGE);
ALTER INDEX IX_CellChange_Cell ON arc.AuditCellChange REBUILD PARTITION = ALL WITH (DATA_COMPRESSION = PAGE);
-- перевірка:
SELECT OBJECT_SCHEMA_NAME(p.object_id) + N'.' + OBJECT_NAME(p.object_id) AS tbl, i.name, p.partition_number, p.data_compression_desc
FROM sys.partitions AS p JOIN sys.indexes AS i ON i.object_id = p.object_id AND i.index_id = p.index_id
WHERE i.type = 2 AND p.data_compression = 0 AND OBJECTPROPERTY(p.object_id, 'IsUserTable') = 1;
```
