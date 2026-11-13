-- SEED-UIEN ── en catalog: MERGE of #UiEn (rows from seed/features/*.sql) ──
-- Same statement as the former en MERGE of 00-prelude.sql, now reading the temp table the feature
-- files filled. Runs BEFORE the #I18N MERGE below: that one takes the Scope of a translation from
-- its en row.
DECLARE @textUpdates int, @removed int, @inserted int;
SELECT @textUpdates = TextUpdates, @removed = Removed FROM #UiEnCounts;
MERGE sys_ecr.UiString AS t
USING (SELECT [Key], Lang, Val, Scope FROM #UiEn) AS s
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());

-- ⛔ Без цього нові ключі, додані СЮДИ (а не через
-- `PUT /api/v1/ui-strings/{lang}/{key}`), лишають Revision незмінним:
-- `SetUiStringHandler` інкрементує його сам (R-B7), а цей MERGE — ні. Клієнт
-- із чинним ETag (`uiStrings:{lang}:{scope}:{revision}` у localStorage)
-- отримує 304 на СТАРУ версію каталогу — свіжі рядки лежать у таблиці, але
-- ніколи не доїжджають до екрана, доки хтось не відкриє `/admin/ui-strings`
-- і не збереже той самий ключ вручну. Живий доказ: `methodologies.*` з
-- директиви «обов'язкові вхідні колонки» (Q-306) — у базі є, на екрані досі
-- `⟦methodologies.requiredInputs⟧`.
--
-- ⚠ Умовно, а не завжди: `SeedRunner` виконується на КОЖНОМУ старті
-- застосунку (`02-contracts.md` §14), і безумовний інкремент означав би
-- зайве validation-round-trip для кожного клієнта на кожному рестарті, навіть
-- коли жодного нового рядка не додалося. `@@ROWCOUNT` після `MERGE` — це
-- кількість щойно вставлених рядків (клаузи `WHEN MATCHED` тут немає).
-- `@textUpdates` і `@removed` — те саме для секцій над MERGE: без інкременту
-- кеш `ui:{lang}:{scope}:{revision}` і ETag клієнта тримали б старий каталог.
SET @inserted = @@ROWCOUNT;
IF @inserted > 0 OR @textUpdates > 0 OR @removed > 0
BEGIN
    UPDATE sys_ecr.UiStringRevision
    SET Revision = Revision + 1, ModifiedAt = SYSUTCDATETIME()
    WHERE Id = 1;
END
DROP TABLE #UiEn;
DROP TABLE #UiEnCounts;
GO
-- SEED-UIEN ── кінець секції ──

-- SEED-SPLIT ── changed and removed texts from seed/features files ──
-- Same condition as the shared lists in 00-prelude.sql: a row is updated/deleted only while it still holds
-- the old value (an administrator edit from /admin/ui-strings is kept). Revision is bumped only if a row
-- changed. Both tables may be empty - then the batch changes nothing.
DECLARE @featureTextUpdates int, @featureRemoved int;
UPDATE t
   SET Value = s.NewVal, ModifiedAt = SYSUTCDATETIME()
  FROM sys_ecr.UiString AS t
  JOIN #UiChanged AS s
    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
 WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;
SET @featureTextUpdates = @@ROWCOUNT;
DELETE t
  FROM sys_ecr.UiString AS t
  JOIN #UiRemoved AS s
    ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
 WHERE t.Value = s.OldVal COLLATE Latin1_General_BIN2;
SET @featureRemoved = @@ROWCOUNT;
IF @featureTextUpdates > 0 OR @featureRemoved > 0
BEGIN
    UPDATE sys_ecr.UiStringRevision
    SET Revision = Revision + 1, ModifiedAt = SYSUTCDATETIME()
    WHERE Id = 1;
END
DROP TABLE #UiChanged;
DROP TABLE #UiRemoved;
GO
-- SEED-SPLIT ── кінець секції ──

-- Лише відсутні пари (ключ, мова); область — з en-рядка.
MERGE sys_ecr.UiString AS t
USING (
    SELECT v.[Key], v.Lang, v.Val, e.Scope
      FROM #I18N AS v
      JOIN sys_ecr.UiString AS e ON e.[Key] = v.[Key] AND e.LanguageCode = N'en'
) AS s
   ON t.[Key] = s.[Key] AND t.LanguageCode = s.Lang
WHEN NOT MATCHED THEN INSERT ([Key], LanguageCode, Value, Scope, ModifiedAt)
     VALUES (s.[Key], s.Lang, s.Val, s.Scope, SYSUTCDATETIME());

IF @@ROWCOUNT > 0
BEGIN
    UPDATE sys_ecr.UiStringRevision
    SET Revision = Revision + 1, ModifiedAt = SYSUTCDATETIME()
    WHERE Id = 1;
END

DROP TABLE #I18N;
-- ── I18N: кінець секції ──────────────────────────────────────────────────
GO

-- ── Опис звіту: одна державна форма з каталогу ФВ-10.7 ───────────────────
-- ⛔ Тут `IEC`; ще три (ECR230_A1/B1/B4) — у секції COLL:an14-ecr230. `rpt.ReportDef` і
-- `rpt.ReportVersion` не створювало НІЩО — ні код, ні seed, ні тести, — тому
-- `POST /reports/{code}/build` відмовляв `ECR-RPT-0404` на будь-який код:
-- звітність існувала і не могла спрацювати жодного разу. Одна реальна форма
-- в seed робить чисту базу здатною побудувати зріз одразу, а не після того,
-- як хтось здогадається завести опис руками (директива №09, W7).
--
-- ⚠ `IEC` — з каталогу державних форм ТЗ (`ФВ-10.7`): Industrial
-- Environmental Control, квартальна. Форми 230 A1/B1/B4 заведено в секції
-- COLL:an14-ecr230; решта (PermitInfo,
-- 20986 Primary Water Use, 2-ТП водгосп, IEC Water, 2-ТП відходи) сюди НЕ
-- йдуть: seed, що заводить сім форм, кожна з яких описана тими самими
-- п'ятьма колонками, виглядав би готовим каталогом і не був би ним —
-- `ФВ-10.9` вимагає для кожної форми ВЛАСНОГО критерію звірки. Решта
-- заводиться через `POST /api/v1/reports` (`Report.EditDefinition`).
MERGE rpt.ReportDef AS t
USING (VALUES (N'IEC',
               N'{"en":"Industrial Environmental Control (quarterly)","ru":"Производственный экологический контроль (квартал)","kz":"Өндірістік экологиялық бақылау (тоқсан)"}',
               1)) AS s (Code, NameL10n, IsRegulatory)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameL10n, IsRegulatory, IsActive)
     VALUES (s.Code, s.NameL10n, s.IsRegulatory, 1);
GO

-- ⚠ Версія одразу `Published` (Status = 1), а не чернетка: сховище описів
-- бере ЛИШЕ опубліковане (`IReportDefinitionStore.FindCurrentVersionAsync`),
-- і чернетка в seed дала б рівно те, від чого seed і рятує, — опис, за яким
-- побудова однаково відмовляє.
--
-- ⚠ Колонки — ті самі п'ять, які будівник зрізу справді пише в
-- `rpt.ReportRow` (`ReportSnapshotBuilder.AggregateAsync`), і в тому форматі,
-- який читає `ReportColumnSpec.Parse`. Опис, що обіцяє колонки, яких у зрізі
-- не буде, гірший за відсутній.
MERGE rpt.ReportVersion AS t
USING (
    SELECT d.Id AS ReportDefId, v.[Version], v.ColumnsJson, v.RulesJson
    FROM (VALUES (N'IEC', N'1.0',
                  N'[{"code":"DocumentId","kind":"number"},{"code":"RowKey","kind":"text"},{"code":"OutputCode","kind":"text"},{"code":"Value","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]',
                  N'{"rowSource":"CalculationResults"}'))
         AS v (Code, [Version], ColumnsJson, RulesJson)
    JOIN rpt.ReportDef AS d ON d.Code = v.Code
) AS s
ON t.ReportDefId = s.ReportDefId AND t.[Version] = s.[Version]
WHEN NOT MATCHED THEN INSERT (ReportDefId, [Version], Status, ColumnsJson, RulesJson, CreatedAt)
     VALUES (s.ReportDefId, s.[Version], 1, s.ColumnsJson, s.RulesJson, SYSUTCDATETIME());
GO

-- COLL:an14-ecr230 ── опис форм 230 A1/B1/B4 (ФВ-10.7, AN-14); власна секція ──
-- ⚠ Колонки — лише коди джерела CalculationResults (ReportSourceColumns): RDL-«колонки»
-- (SUM_HP_Purge_Value, FG_Makat_*) — це значення OutputCode у довгому форматі, а не колонки опису.
-- ⚠ Коди ECR230_A1/B1/B4 і підписи en/ru — дефолт із заголовків RDL; KZ у RDL немає, не вигадано
-- (назва колонки без kz падає на en, ReportColumnNames.Of). Критерій звірки ФВ-10.9, зміст OutputCode,
-- мапінг періоду «квартал» ↔ PeriodKey і подієві рамки (дати епізодів) — прогалини замовника/коду.
MERGE rpt.ReportDef AS t
USING (VALUES
  (N'ECR230_A1', N'{"en":"Unit A1-230 Flares (quarterly)","ru":"Установка 230 A1 – Факельная система (квартал)"}', 1),
  (N'ECR230_B1', N'{"en":"Unit B1-230 Flares (quarterly)","ru":"Установка 230 B1 – Факельная система (квартал)"}', 1),
  (N'ECR230_B4', N'{"en":"Unit B4-230 Flares (quarterly)","ru":"Установка B4-230 – Факельная система (квартал)"}', 1)
) AS s (Code, NameL10n, IsRegulatory)
ON t.Code = s.Code
WHEN NOT MATCHED THEN INSERT (Code, NameL10n, IsRegulatory, IsActive)
     VALUES (s.Code, s.NameL10n, s.IsRegulatory, 1);
GO

MERGE rpt.ReportVersion AS t
USING (
    SELECT d.Id AS ReportDefId, v.[Version], v.ColumnsJson, v.RulesJson
    FROM (VALUES
        (N'ECR230_A1', N'1.0', N'[{"code":"ProjectCode","kind":"text","nameL10n":{"en":"Unit / project","ru":"Установка"}},{"code":"PeriodKey","kind":"number","nameL10n":{"en":"Period","ru":"Период"}},{"code":"RowKey","kind":"text","nameL10n":{"en":"Stream / event row","ru":"Поток / событие"}},{"code":"OutputCode","kind":"text","nameL10n":{"en":"Indicator","ru":"Показатель"}},{"code":"Value","kind":"number","nameL10n":{"en":"Value","ru":"Значение"}},{"code":"UnitCode","kind":"text","nameL10n":{"en":"Unit of measure","ru":"Единица измерения"}},{"code":"DocumentId","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]', N'{"rowSource":"CalculationResults"}'),
        (N'ECR230_B1', N'1.0', N'[{"code":"ProjectCode","kind":"text","nameL10n":{"en":"Unit / project","ru":"Установка"}},{"code":"PeriodKey","kind":"number","nameL10n":{"en":"Period","ru":"Период"}},{"code":"RowKey","kind":"text","nameL10n":{"en":"Stream / event row","ru":"Поток / событие"}},{"code":"OutputCode","kind":"text","nameL10n":{"en":"Indicator","ru":"Показатель"}},{"code":"Value","kind":"number","nameL10n":{"en":"Value","ru":"Значение"}},{"code":"UnitCode","kind":"text","nameL10n":{"en":"Unit of measure","ru":"Единица измерения"}},{"code":"DocumentId","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]', N'{"rowSource":"CalculationResults"}'),
        (N'ECR230_B4', N'1.0', N'[{"code":"ProjectCode","kind":"text","nameL10n":{"en":"Unit / project","ru":"Установка"}},{"code":"PeriodKey","kind":"number","nameL10n":{"en":"Period","ru":"Период"}},{"code":"RowKey","kind":"text","nameL10n":{"en":"Stream / event row","ru":"Поток / событие"}},{"code":"OutputCode","kind":"text","nameL10n":{"en":"Indicator","ru":"Показатель"}},{"code":"Value","kind":"number","nameL10n":{"en":"Value","ru":"Значение"}},{"code":"UnitCode","kind":"text","nameL10n":{"en":"Unit of measure","ru":"Единица измерения"}},{"code":"DocumentId","kind":"number"},{"code":"SubstanceEntryId","kind":"number"}]', N'{"rowSource":"CalculationResults"}')
    ) AS v (Code, [Version], ColumnsJson, RulesJson)
    JOIN rpt.ReportDef AS d ON d.Code = v.Code
) AS s
ON t.ReportDefId = s.ReportDefId AND t.[Version] = s.[Version]
WHEN NOT MATCHED THEN INSERT (ReportDefId, [Version], Status, ColumnsJson, RulesJson, CreatedAt)
     VALUES (s.ReportDefId, s.[Version], 1, s.ColumnsJson, s.RulesJson, SYSUTCDATETIME());
GO
-- COLL:an14-ecr230 ── кінець секції ──

-- COLL:period0-supersede ── Старі річні прогони нічного перерахунку (період 0) ──
-- ⛔ Одноразове виправлення ДАНИХ, ідемпотентне. До фіксу 44c952c1 прогін «на
-- весь рік» (`PeriodKey = NULL`, нічний розклад) писав результати методологій у
-- `calc.CalculationResult.PeriodKey = 0` (`run.PeriodKey ?? 0` у
-- `CalculationResultStore`). Жодного реального періоду там немає, але прогін
-- лишався `Current` — і річний зріз `rpt.*` (`ReportSnapshotBuilder`,
-- `periodKey = NULL`) підхоплював ці числа поруч із поперіодними.
--
-- ⚠ Рішення HSE301: НЕ видаляти, а зняти актуальність (`Superseded` — той самий
-- стан, що ставить `SwitchCurrentRunAsync`): історія й аудит лишаються, читачі
-- актуального їх більше не бачать. Змінюється лише `calc.CalculationRun.Status`.
--
-- ⚠ Лише прогони, що МАЮТЬ результати в періоді 0 (`EXISTS`). Річний прогін
-- без результатів — законний слід задачі після фіксу («рік без періодів у
-- скоупі»): він нічого не показує, і знімати з нього актуальність на кожному
-- старті (seed виконується щоразу, `StartupSequence`) означало б, що скрипт
-- змінює дані не один раз, а постійно. Після фіксу нових рядків у періоді 0 не
-- з'являється, тож другий і кожен наступний запуск змінює 0 рядків.
--
-- ⚠ SET-опції явно: `UX_CalculationRun_Current` — фільтрований індекс, а
-- `sqlcmd` за замовчуванням має `QUOTED_IDENTIFIER OFF`.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

UPDATE run
SET    run.Status = N'Superseded'
FROM   calc.CalculationRun AS run
WHERE  run.PeriodKey IS NULL
  AND  run.Status = N'Current'
  AND  EXISTS (SELECT 1
               FROM   calc.CalculationResult AS r
               WHERE  r.PeriodKey = 0
                 AND  r.CalculationRunId = run.Id);

PRINT CONCAT(N'period0-supersede: ', @@ROWCOUNT, N' rows');
GO
-- COLL:period0-supersede ── кінець секції ─────────────────────────────────────

-- MIGR:permit-history-repair ── Історія дозволів, яку перенос версії до D-7 лишив на Id старої версії ──
-- ⛔ Одноразове виправлення ДАНИХ, ідемпотентне (аудит 2026-11, D-7). До фіксу D-7 перенос проєкту на нову
-- версію шаблону перемаплював комірки, екземпляри й шапку, а `doc.PermitSegment` і `doc.CellPermitPart` лишав на
-- `TableDefId`/`ColumnDefId`/`HeaderFieldDefId` ВИХІДНОЇ версії: читачі (`PermitHistoryStore`) шукають за Id
-- поточної версії й історії дозволів не бачать.
--
-- Відповідник шукається ЗА КОДАМИ (аркуш + таблиця + колонка; поле шапки) у версії того самого шаблону:
--   • сегменти (рядкові й шапки) — у поточній версії проєкту (`doc.Project.TemplateVersionId`), як і D-7, що
--     перемаплює сегменти й у режимі «з місяця»;
--   • частини місяця — у версії, що діє для ПЕРІОДУ частини (`doc.ProjectTemplateVersion`, RPT-STABLE Part B; без
--     рядків — версія проєкту), і лише коли в документа за цей період/копію екземпляр таблиці стоїть на НОВІЙ
--     таблиці, а на старій — немає (період «з місяця» до X лишається на старих Id разом зі своїм екземпляром).
--
-- ⚠ Консервативно: рядок без відповідника (таблицю/колонку/поле прибрано або перейменовано мапою), рядок, чиє
-- місце вже зайняте (історію додали заново після переносу), і кілька старих рядків на одне нове місце — НЕ
-- чіпаються і НЕ видаляються; їх кількість іде в журнал старту, а кожен — у журнал узгодженості (крок 4). Проєкт під маркером переносу (`migratingTo`) — не
-- чіпається. Після виправлення рядок адресує версію, з якою звіряється умова, тож другий і кожен наступний запуск
-- змінює 0 рядків (сід виконується на кожному старті, `StartupSequence`).
-- Як перевірити руками й що робити з лишком — docs/admin/operations-runbook.md, «Історія дозволів після переносу».
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

DECLARE @changed int = 0;
DECLARE @left int = 0;

-- 1. Рядкові сегменти.
WITH c AS (
    SELECT g.Id, nt.Id AS NewTableId, nc.Id AS NewColumnId,
           COUNT(*) OVER (PARTITION BY g.DocumentId, nt.Id, g.SheetCopyId, g.RowKey, nc.Id, g.ValidFrom) AS SameTarget
    FROM   doc.PermitSegment AS g
    JOIN   doc.Document AS d          ON d.Id = g.DocumentId
    JOIN   doc.Project AS pr          ON pr.Id = d.ProjectId
    JOIN   cfg.TableDef AS ot         ON ot.Id = g.TableDefId
    JOIN   cfg.SheetDef AS os         ON os.Id = ot.SheetDefId
    JOIN   cfg.ColumnDef AS oc        ON oc.Id = g.ColumnDefId AND oc.TableDefId = ot.Id
    JOIN   cfg.TemplateVersion AS ov  ON ov.Id = os.TemplateVersionId
    JOIN   cfg.TemplateVersion AS pv  ON pv.Id = pr.TemplateVersionId AND pv.TemplateId = ov.TemplateId
    JOIN   cfg.SheetDef AS ns         ON ns.TemplateVersionId = pv.Id AND ns.IsDeleted = 0
                                     AND ns.Code = os.Code COLLATE Latin1_General_100_BIN2
    JOIN   cfg.TableDef AS nt         ON nt.SheetDefId = ns.Id AND nt.IsDeleted = 0
                                     AND nt.Code = ot.Code COLLATE Latin1_General_100_BIN2
    JOIN   cfg.ColumnDef AS nc        ON nc.TableDefId = nt.Id AND nc.IsDeleted = 0
                                     AND nc.Code = oc.Code COLLATE Latin1_General_100_BIN2
    WHERE  os.TemplateVersionId <> pr.TemplateVersionId
      AND  ISNULL(CASE WHEN ISJSON(pr.ExternalSettingsJson) = 1
                       THEN JSON_VALUE(pr.ExternalSettingsJson, '$.migratingTo') END, N'') = N''
)
UPDATE g
SET    g.TableDefId = c.NewTableId, g.ColumnDefId = c.NewColumnId
FROM   doc.PermitSegment AS g
JOIN   c ON c.Id = g.Id
WHERE  c.SameTarget = 1
  AND  NOT EXISTS (SELECT 1 FROM doc.PermitSegment AS x
                   WHERE  x.DocumentId = g.DocumentId AND x.TableDefId = c.NewTableId AND x.RowKey = g.RowKey
                     AND  x.ColumnDefId = c.NewColumnId AND x.ValidFrom = g.ValidFrom
                     AND  (x.SheetCopyId = g.SheetCopyId OR (x.SheetCopyId IS NULL AND g.SheetCopyId IS NULL)));
SET @changed += @@ROWCOUNT;

-- 2. Сегменти шапки.
WITH c AS (
    SELECT g.Id, nh.Id AS NewFieldId,
           COUNT(*) OVER (PARTITION BY g.DocumentId, nh.Id, g.ValidFrom) AS SameTarget
    FROM   doc.PermitSegment AS g
    JOIN   doc.Document AS d          ON d.Id = g.DocumentId
    JOIN   doc.Project AS pr          ON pr.Id = d.ProjectId
    JOIN   cfg.HeaderFieldDef AS oh   ON oh.Id = g.HeaderFieldDefId
    JOIN   cfg.TemplateVersion AS ov  ON ov.Id = oh.TemplateVersionId
    JOIN   cfg.TemplateVersion AS pv  ON pv.Id = pr.TemplateVersionId AND pv.TemplateId = ov.TemplateId
    JOIN   cfg.HeaderFieldDef AS nh   ON nh.TemplateVersionId = pv.Id AND nh.IsDeleted = 0
                                     AND nh.Code = oh.Code COLLATE Latin1_General_100_BIN2
    WHERE  oh.TemplateVersionId <> pr.TemplateVersionId
      AND  ISNULL(CASE WHEN ISJSON(pr.ExternalSettingsJson) = 1
                       THEN JSON_VALUE(pr.ExternalSettingsJson, '$.migratingTo') END, N'') = N''
)
UPDATE g
SET    g.HeaderFieldDefId = c.NewFieldId
FROM   doc.PermitSegment AS g
JOIN   c ON c.Id = g.Id
WHERE  c.SameTarget = 1
  AND  NOT EXISTS (SELECT 1 FROM doc.PermitSegment AS x
                   WHERE  x.DocumentId = g.DocumentId AND x.HeaderFieldDefId = c.NewFieldId AND x.ValidFrom = g.ValidFrom);
SET @changed += @@ROWCOUNT;

-- 3. Частини місяця (партиціоновані: PeriodKey у кожній умові).
WITH c AS (
    SELECT p.PeriodKey, p.Id, nt.Id AS NewTableId, nc.Id AS NewColumnId,
           MIN(p.ColumnDefId) OVER (PARTITION BY p.PeriodKey, p.DocumentId, nt.Id, p.SheetCopyId, p.RowKey, nc.Id) AS MinSource,
           MAX(p.ColumnDefId) OVER (PARTITION BY p.PeriodKey, p.DocumentId, nt.Id, p.SheetCopyId, p.RowKey, nc.Id) AS MaxSource
    FROM   doc.CellPermitPart AS p
    JOIN   doc.Document AS d          ON d.Id = p.DocumentId
    JOIN   doc.Project AS pr          ON pr.Id = d.ProjectId
    OUTER APPLY (SELECT TOP (1) v.TemplateVersionId
                 FROM   doc.ProjectTemplateVersion AS v
                 WHERE  v.ProjectId = pr.Id AND v.FromPeriodKey <= p.PeriodKey
                 ORDER  BY v.FromPeriodKey DESC) AS ev
    JOIN   cfg.TableDef AS ot         ON ot.Id = p.TableDefId
    JOIN   cfg.SheetDef AS os         ON os.Id = ot.SheetDefId
    JOIN   cfg.ColumnDef AS oc        ON oc.Id = p.ColumnDefId AND oc.TableDefId = ot.Id
    JOIN   cfg.TemplateVersion AS ov  ON ov.Id = os.TemplateVersionId
    JOIN   cfg.TemplateVersion AS pv  ON pv.Id = COALESCE(ev.TemplateVersionId, pr.TemplateVersionId)
                                     AND pv.TemplateId = ov.TemplateId
    JOIN   cfg.SheetDef AS ns         ON ns.TemplateVersionId = pv.Id AND ns.IsDeleted = 0
                                     AND ns.Code = os.Code COLLATE Latin1_General_100_BIN2
    JOIN   cfg.TableDef AS nt         ON nt.SheetDefId = ns.Id AND nt.IsDeleted = 0
                                     AND nt.Code = ot.Code COLLATE Latin1_General_100_BIN2
    JOIN   cfg.ColumnDef AS nc        ON nc.TableDefId = nt.Id AND nc.IsDeleted = 0
                                     AND nc.Code = oc.Code COLLATE Latin1_General_100_BIN2
    WHERE  os.TemplateVersionId <> pv.Id
      AND  ISNULL(CASE WHEN ISJSON(pr.ExternalSettingsJson) = 1
                       THEN JSON_VALUE(pr.ExternalSettingsJson, '$.migratingTo') END, N'') = N''
      AND  NOT EXISTS (SELECT 1 FROM doc.TableInstance AS i
                       WHERE  i.PeriodKey = p.PeriodKey AND i.DocumentId = p.DocumentId AND i.TableDefId = p.TableDefId
                         AND  (i.SheetCopyId = p.SheetCopyId OR (i.SheetCopyId IS NULL AND p.SheetCopyId IS NULL)))
      AND  EXISTS (SELECT 1 FROM doc.TableInstance AS i
                   WHERE  i.PeriodKey = p.PeriodKey AND i.DocumentId = p.DocumentId AND i.TableDefId = nt.Id
                     AND  (i.SheetCopyId = p.SheetCopyId OR (i.SheetCopyId IS NULL AND p.SheetCopyId IS NULL)))
)
UPDATE p
SET    p.TableDefId = c.NewTableId, p.ColumnDefId = c.NewColumnId
FROM   doc.CellPermitPart AS p
JOIN   c ON c.PeriodKey = p.PeriodKey AND c.Id = p.Id
WHERE  c.MinSource = c.MaxSource
  AND  NOT EXISTS (SELECT 1 FROM doc.CellPermitPart AS x
                   WHERE  x.PeriodKey = p.PeriodKey AND x.DocumentId = p.DocumentId AND x.TableDefId = c.NewTableId
                     AND  x.RowKey = p.RowKey AND x.ColumnDefId = c.NewColumnId
                     AND  (x.SheetCopyId = p.SheetCopyId OR (x.SheetCopyId IS NULL AND p.SheetCopyId IS NULL)));
SET @changed += @@ROWCOUNT;

-- 4. Лишок — рядки, яких скрипт не чіпав: сегменти не на версії проєкту; частини не на версії свого періоду, у
-- яких за цей період/копію немає екземпляра їхньої (старої) таблиці. ⛔ Не видаляються й не вгадуються (рішення
-- координатора 12.11): кожен — знахідка `PERMIT_HISTORY_STALE_VERSION` у журналі узгодженості (`aud.ConsistencyIssue`,
-- той самий журнал, що й нічна перевірка; екран «Узгодженість»), і їх кількість — у журнал старту. Повторний старт
-- нової знахідки не додає, доки є відкрита на той самий рядок (`ResolvedAt IS NULL`).
DECLARE @stale TABLE (EntityType nvarchar(64) NOT NULL, EntityId bigint NOT NULL, PeriodKey int NULL,
                      DocumentId bigint NOT NULL, DetailsJson nvarchar(max) NOT NULL,
                      PRIMARY KEY (EntityType, EntityId));

INSERT @stale (EntityType, EntityId, PeriodKey, DocumentId, DetailsJson)
SELECT N'doc.PermitSegment', g.Id, NULL, g.DocumentId,
       CONCAT(N'{"documentId":', g.DocumentId, N',"tableDefId":', g.TableDefId, N',"columnDefId":', g.ColumnDefId,
              N',"projectVersionId":', pr.TemplateVersionId, N',"rowVersionId":', s.TemplateVersionId, N'}')
FROM   doc.PermitSegment AS g
JOIN   doc.Document AS d   ON d.Id = g.DocumentId
JOIN   doc.Project AS pr   ON pr.Id = d.ProjectId
JOIN   cfg.TableDef AS t   ON t.Id = g.TableDefId
JOIN   cfg.SheetDef AS s   ON s.Id = t.SheetDefId
WHERE  s.TemplateVersionId <> pr.TemplateVersionId;

INSERT @stale (EntityType, EntityId, PeriodKey, DocumentId, DetailsJson)
SELECT N'doc.PermitSegment', g.Id, NULL, g.DocumentId,
       CONCAT(N'{"documentId":', g.DocumentId, N',"headerFieldDefId":', g.HeaderFieldDefId,
              N',"projectVersionId":', pr.TemplateVersionId, N',"rowVersionId":', h.TemplateVersionId, N'}')
FROM   doc.PermitSegment AS g
JOIN   doc.Document AS d        ON d.Id = g.DocumentId
JOIN   doc.Project AS pr        ON pr.Id = d.ProjectId
JOIN   cfg.HeaderFieldDef AS h  ON h.Id = g.HeaderFieldDefId
WHERE  h.TemplateVersionId <> pr.TemplateVersionId;

INSERT @stale (EntityType, EntityId, PeriodKey, DocumentId, DetailsJson)
SELECT N'doc.CellPermitPart', p.Id, p.PeriodKey, p.DocumentId,
       CONCAT(N'{"documentId":', p.DocumentId, N',"periodKey":', p.PeriodKey, N',"tableDefId":', p.TableDefId,
              N',"columnDefId":', p.ColumnDefId, N',"partNo":', p.PartNo,
              N',"periodVersionId":', COALESCE(ev.TemplateVersionId, pr.TemplateVersionId), N',"rowVersionId":', s.TemplateVersionId, N'}')
FROM   doc.CellPermitPart AS p
JOIN   doc.Document AS d   ON d.Id = p.DocumentId
JOIN   doc.Project AS pr   ON pr.Id = d.ProjectId
JOIN   cfg.TableDef AS t   ON t.Id = p.TableDefId
JOIN   cfg.SheetDef AS s   ON s.Id = t.SheetDefId
OUTER APPLY (SELECT TOP (1) v.TemplateVersionId
             FROM   doc.ProjectTemplateVersion AS v
             WHERE  v.ProjectId = pr.Id AND v.FromPeriodKey <= p.PeriodKey
             ORDER  BY v.FromPeriodKey DESC) AS ev
WHERE  s.TemplateVersionId <> COALESCE(ev.TemplateVersionId, pr.TemplateVersionId)
  AND  NOT EXISTS (SELECT 1 FROM doc.TableInstance AS i
                   WHERE  i.PeriodKey = p.PeriodKey AND i.DocumentId = p.DocumentId AND i.TableDefId = p.TableDefId
                     AND  (i.SheetCopyId = p.SheetCopyId OR (i.SheetCopyId IS NULL AND p.SheetCopyId IS NULL)));

SET @left = (SELECT COUNT(*) FROM @stale);

INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message, DetailsJson, PeriodKey)
SELECT SYSUTCDATETIME(), 2, N'PERMIT_HISTORY_STALE_VERSION', x.EntityType, x.EntityId,
       CONCAT(N'Історія дозволів документа ', x.DocumentId,
              N' лишилась на таблиці/колонці/полі шапки іншої версії шаблону, ніж версія проєкту (перенос до D-7), ',
              N'і відповідника за кодом немає. Рядок не змінено й не видалено: додайте історію заново або видаліть рядок ',
              N'(runbook, розділ 16.2).'),
       x.DetailsJson, x.PeriodKey
FROM   @stale AS x
WHERE  NOT EXISTS (SELECT 1 FROM aud.ConsistencyIssue AS c
                   WHERE  c.RuleCode = N'PERMIT_HISTORY_STALE_VERSION' AND c.EntityType = x.EntityType
                     AND  c.EntityId = x.EntityId AND c.ResolvedAt IS NULL
                     AND  (c.PeriodKey = x.PeriodKey OR (c.PeriodKey IS NULL AND x.PeriodKey IS NULL)));

PRINT CONCAT(N'permit-history-repair: ', @changed, N' rows; left unmatched: ', @left);
GO
-- MIGR:permit-history-repair ── кінець секції ───────────────────────────────

-- FP:sysadmin-rights ── Системний адміністратор: адмін-права керування (рішення людини 2026-10-11) ──
-- ✎ Прохід «від першого обличчя» (FIRST-PERSON-2026-10-11, P2-2): вбудований `SystemAdministrator`
-- («усе, крім небезпечного») не бачив Security, Mapping, Pipeline, Notifications, Campaign і не
-- публікував методики — назва ролі обіцяла протилежне. Рішення людини (квіз 11.10): роль має
-- ВСІ адмін-права керування.
--
-- ⛔ Поіменно й ОКРЕМИМ MERGE, а НЕ послабленням фільтра `IsDangerous = 0` у блоці роздач вище:
-- шаблон `%` решти ролей небезпечного й далі не бере (`Approver` ×
-- `Report.EditDefinition` — окремий MERGE `D-203`; `BootstrapAdministrator` — два права).
-- Той самий механізм, що й у двох наявних поіменних винятків.
--
-- ⚠ Видано ВІСІМ прав адмін-керування (+2 нижче, 2026-10-12) (`D-207`: розділення обов'язків адміністраторів
-- не обмежуємо; `D-121`: носій `Security.ManageRoles` і так видає будь-яке право):
--   Security.ManageUsers · Security.ManageRoles · Security.Simulate  — користувачі, ролі, «View as»;
--   Integration.Manage — джерела, мапінг, конвеєр; System.ManageNotifications — канали й SMTP;
--   System.RunJob — ручний запуск системних задач; Report.ViewCampaign — огляд кампанії;
--   Calculation.Publish — публікація методик.
-- Захист «чотирьох очей» стоїть у точці ВИКОРИСТАННЯ, не видачі, і цей MERGE його не чіпає:
-- публікація методики автором її створення відмовляється (`ФВ-9.6`, `D-226`), Reopen вимагає
-- причини, симуляція пишеться в аудит, а ціль симуляції з небезпечним правом заборонена.
--
-- ✎ 2026-10-12 (рішення людини, квіз 12.10, №1): до восьми додано ще ДВА — `Period.Reopen`
-- (повторне відкриття закритого періоду) і `Document.ChangeKey` (зміна бізнес-ключа), разом ДЕСЯТЬ.
-- Вони йдуть тим самим MERGE (NOT MATCHED, без міграції) і тією самою подією `RolePermissionsGranted`:
-- наявна база отримує лише відсутні пари, подія перелічує рівно те, що додано. Захист — у точці
-- використання: Reopen вимагає причини й гранта на проєкт, зміна ключа пишеться в аудит.
--
-- ⛔ НЕ видано навмисно (на рішення людини):
--   Report.EditDefinition (авторство державної форми; `D-203`, лише Approver).
--
-- ⚠ MERGE … WHEN NOT MATCHED: наявна база отримує пари на наступному старті (`SeedRunner`)
-- без міграції схеми; право, зняте адміністратором з вбудованої ролі, повторний сід поверне
-- (так само, як і решту вбудованих пар; потрібен інший набір — власна роль).
--
-- ✎ Видача адмін-прав — подія безпеки: знімок пар ролі ДО MERGE; у журнал (`aud.SecurityEvent`,
-- тип `RolePermissionsGranted`) іде лише те, що додав саме він, тож повторний старт тиші не рве.
IF OBJECT_ID(N'tempdb..#FpSysAdminRightsBefore') IS NOT NULL DROP TABLE #FpSysAdminRightsBefore;
SELECT rp.RoleId, rp.PermissionCode INTO #FpSysAdminRightsBefore
FROM sec.RolePermission AS rp
JOIN sec.Role AS r ON r.Id = rp.RoleId AND r.IsBuiltIn = 1 AND r.Code = N'SystemAdministrator';
MERGE sec.RolePermission AS t
USING (
    SELECT r.Id AS RoleId, p.Code AS PermissionCode
    FROM (VALUES
        (N'Security.ManageUsers'), (N'Security.ManageRoles'), (N'Security.Simulate'),
        (N'Integration.Manage'), (N'System.ManageNotifications'), (N'System.RunJob'),
        (N'Report.ViewCampaign'), (N'Calculation.Publish'),
        -- ✎ 2026-10-12 (рішення людини, квіз 12.10, №1; D-388 доповнено): ще два, у ТІЙ САМІЙ секції.
        (N'Period.Reopen'), (N'Document.ChangeKey')
    ) AS m (Code)
    JOIN sec.Permission AS p ON p.Code = m.Code
    CROSS JOIN sec.Role AS r
    WHERE r.Code = N'SystemAdministrator' AND r.IsBuiltIn = 1 -- U1-04
) AS s
ON t.RoleId = s.RoleId AND t.PermissionCode = s.PermissionCode
WHEN NOT MATCHED THEN INSERT (RoleId, PermissionCode) VALUES (s.RoleId, s.PermissionCode);
GO
-- ✎ Одна подія безпеки на кожну видачу: роль, кількість, перелік кодів прав, джерело. Без
-- користувачів (система: ChangedByUserId = 0, як у решти системних записів). Нових пар немає — немає й події.
INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
SELECT SYSUTCDATETIME(), N'RolePermissionsGranted', NULL, r.Id,
       CONCAT(N'{"role":"SystemAdministrator","source":"seed FP:sysadmin-rights","count":', COUNT(*),
              N',"permissions":["',
              STUFF((SELECT N'","' + CAST(x.PermissionCode AS nvarchar(max)) FROM sec.RolePermission AS x WHERE x.RoleId = r.Id AND NOT EXISTS (SELECT 1 FROM #FpSysAdminRightsBefore AS xb WHERE xb.RoleId = x.RoleId AND xb.PermissionCode = x.PermissionCode) ORDER BY x.PermissionCode FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 3, N''),
              N'"]}'),
       0, NULL
FROM sec.Role AS r
JOIN sec.RolePermission AS rp ON rp.RoleId = r.Id
WHERE r.Code = N'SystemAdministrator' AND r.IsBuiltIn = 1
  AND NOT EXISTS (SELECT 1 FROM #FpSysAdminRightsBefore AS b
                  WHERE b.RoleId = rp.RoleId AND b.PermissionCode = rp.PermissionCode)
GROUP BY r.Id;
DROP TABLE #FpSysAdminRightsBefore;
GO
-- FP:sysadmin-rights ── кінець секції ────────────────────────────────────────

-- TC:discuss ── право `Document.Discuss` усім ролям, що мають `Document.View` (чат таблиці, рішення людини 29.10) ──
-- ⛔ ОДНОРАЗОВО, а не MERGE на кожен старт: адміністратор вправі зняти право з ролі, і повторний сід не повинен
-- його повертати. Позначка виконання — подія безпеки `RolePermissionsGranted` з `"source":"seed TC:discuss"`; є подія —
-- секція нічого не робить. Роль, створена ПІСЛЯ першого сіду, права не отримує (адміністратор видає свідомо).
-- `SystemAdministrator` отримав обидва права шаблоном `%` ще в блоці роздач вище (право не небезпечне).
IF NOT EXISTS (SELECT 1 FROM aud.SecurityEvent
               WHERE EventType = N'RolePermissionsGranted' AND DetailsJson LIKE N'%"source":"seed TC:discuss"%')
   AND EXISTS (SELECT 1 FROM sec.Permission WHERE Code = N'Document.Discuss')
BEGIN
    DECLARE @tcGranted TABLE (RoleId int NOT NULL);

    INSERT INTO sec.RolePermission (RoleId, PermissionCode)
    OUTPUT inserted.RoleId INTO @tcGranted (RoleId)
    SELECT v.RoleId, N'Document.Discuss'
    FROM sec.RolePermission AS v
    WHERE v.PermissionCode = N'Document.View'
      AND NOT EXISTS (SELECT 1 FROM sec.RolePermission AS d
                      WHERE d.RoleId = v.RoleId AND d.PermissionCode = N'Document.Discuss');

    INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
    SELECT SYSUTCDATETIME(), N'RolePermissionsGranted', NULL, g.RoleId,
           CONCAT(N'{"permission":"Document.Discuss","source":"seed TC:discuss","roleId":', g.RoleId, N'}'),
           0, NULL
    FROM @tcGranted AS g;

    -- Хоч одна подія-позначка потрібна навіть коли нових пар не було (усі ролі вже мали право): без неї секція
    -- повторювалась би на кожному старті.
    IF NOT EXISTS (SELECT 1 FROM @tcGranted)
        INSERT INTO aud.SecurityEvent (ChangedAt, EventType, TargetUserId, TargetRoleId, DetailsJson, ChangedByUserId, CorrelationId)
        VALUES (SYSUTCDATETIME(), N'RolePermissionsGranted', NULL, NULL,
                N'{"permission":"Document.Discuss","source":"seed TC:discuss","count":0}', 0, NULL);
END
GO
-- TC:discuss ── кінець секції ──

-- B74:project-templates ── ремонт подвійного запису PT01 (ADR docs/build/ADR-B74-PROJECT-TEMPLATES.md, Q2) ──
-- ⚠ Ідемпотентно на кожен старт. Обробники пишуть doc.ProjectTemplate і doc.Document.TemplateId самі; ця секція лише
-- доповнює рядки, створені повз них (фікстури EF, DBA, старий білд у вікні відкату): основний шаблон проєкту = шаблон
-- Project.TemplateVersionId. Секція зникає в P6 разом із колонкою. Сід іде після міграцій, тож PT01 уже застосована.
INSERT INTO doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
SELECT p.Id, v.TemplateId, p.TemplateVersionId
FROM doc.Project AS p
JOIN cfg.TemplateVersion AS v ON v.Id = p.TemplateVersionId
WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate AS x WHERE x.ProjectId = p.Id AND x.TemplateId = v.TemplateId);

UPDATE d SET TemplateId = v.TemplateId
FROM doc.Document AS d
JOIN doc.Project AS p ON p.Id = d.ProjectId
JOIN cfg.TemplateVersion AS v ON v.Id = p.TemplateVersionId
WHERE d.TemplateId IS NULL;
GO
-- B74:project-templates ── кінець секції ──