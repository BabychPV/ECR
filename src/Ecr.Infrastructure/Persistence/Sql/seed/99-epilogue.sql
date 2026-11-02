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
