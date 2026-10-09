# Пропозиції для DBA — з вимірами (2026-10-08)

Єдине місце для пропозицій, що потребують DDL або рішення DBA. Усі рядки мають статус **proposed**: у `Sql/` і в міграції EF нічого не додано (D-66 — застосунок без DDL; D-264 — DBA на замовнику). Скрипти нижче — **приклади**, не виконувались на прод-базі. Задачі в черзі — `docs/build/WORK-QUEUE.md`, `DB-5`, `DB-6`. Походження: `docs/build/TODO-REMAINING.md` B3.6/B3.7.

| # | Пропозиція | Хто вирішує | Статус |
|---|---|---|---|
| 1 | Індекс `IX_CellChange_Doc_Period_ChangedAt` на `aud.CellChange` | замовник/DBA (К10) | proposed |
| 2 | `LOCK_ESCALATION` на `doc.CellValue` / `doc.TableRow` | DBA (замовника не питаємо — рішення координатора 08.10) | proposed |

## 1. Індекс `aud.CellChange` для resultsStale (DB-5)

| Поле | Зміст |
|---|---|
| Навіщо | Лічильники «результати застаріли» (`staleResultsCount`, `/documents/summary`) і фільтр `GET /documents?resultsStale=true` ходять по аудиту комірок (`aud.CellChange`) |
| Виміри | 2000 документів / 504 тис. рядків `aud.CellChange`, усі застарілі (гірший випадок). Фільтр/лічильник resultsStale по проєкту: ≈1,0 с -> ≈0,35 с. Лічильник «мої»: 0,4-0,6 с -> ≈0,10 с. Позначка на сторінку документів: 16-26 мс -> 10-13 мс. На 10x обсязі без індексу ≈10 с (екстраполяція, не вимір) |
| Ціна | Ще один індекс на найбільшій таблиці аудиту; додатковий запис на кожну правку комірки; DDL на партиціонованій таблиці (вікно на побудову індексу залежить від обсягу) |
| Ризик | Низький для даних (лише індекс). Побудова на великій таблиці тримає ресурси; рекомендується поза піком. Скрипт ідемпотентний |
| Поточне пом'якшення | Кеш лічильників, TTL 45 с (`DocumentListSummaryStore.StaleCountsTtl`); `GET /documents?resultsStale=true` не кешується, rate limit для нього немає (борг B3.7) |
| Хто вирішує | Замовник/DBA (К10) |
| Статус | proposed |

Приклад скрипта (потрібен `sqlcmd -I`, бо індекс вимагає `QUOTED_IDENTIFIER ON`):

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
| Виміри | Раніше: PATCH комірки в інший проєкт через ≈30 с давав 500. Тепер (lane `fix-patch-lock-timeout`, b15f6784): 409 `ECR-DOC-4091` lockTimeout за ≈8 с (`LOCK_TIMEOUT 8000`). Для Land (138 тис. значень, 25-60 с) вікно мале. З `DISABLE` очікування: перенос блокує лише свої рядки, PATCH в інші проєкти не чекає взагалі (очікування, не вимір) |
| Ціна | Більше пам'яті під блокування (по рядку замість ескалації) на час переносу; потрібен DDL/міграція і дозвіл DBA |
| Ризик | Середній: на великих переносах зростає кількість блокувань у пам'яті сервера. На партиціонованих таблицях можна розглянути `AUTO` (ескалація до партиції) |
| Альтернатива | Окремі транзакції по таблицях у `MigrationStore` — порушує атомарність переносу, **не рекомендується** |
| Хто вирішує | DBA; замовнику не питаємо (рішення координатора 08.10) |
| Статус | proposed |

Приклад скрипта:

```sql
ALTER TABLE doc.CellValue SET (LOCK_ESCALATION = DISABLE);
ALTER TABLE doc.TableRow  SET (LOCK_ESCALATION = DISABLE);
-- для партиціонованих таблиць альтернатива:
-- ALTER TABLE doc.CellValue SET (LOCK_ESCALATION = AUTO);
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
| Статус | proposed |

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
