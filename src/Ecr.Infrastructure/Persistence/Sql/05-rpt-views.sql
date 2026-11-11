-- ⚠ SET-опції задаються ЯВНО і першими.
-- `sqlcmd` за замовчуванням має `QUOTED_IDENTIFIER OFF`, а `SqlClient` — `ON`.
-- Через це скрипт, який проходить у тестах (їх виконує SqlClient), падає в
-- розгортанні (його виконує DBA через sqlcmd, `09-commands.md` §3) на будь-якій
-- таблиці з фільтрованим індексом або індексованою в'юхою. Опція ще й
-- ЗАПАМ'ЯТОВУЄТЬСЯ в момент створення процедури — тому її треба поставити до
-- першого `CREATE`, а не «якось у сесії».
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- src/Ecr.Infrastructure/Persistence/Sql/05-rpt-views.sql
-- Генерується під кожен звіт. ФІЛЬТР ЗА СТАТУСОМ — У ВʼЮСІ, не в RDL (D-65):
-- інакше його одного дня забудуть поставити.
CREATE OR ALTER VIEW rpt.v_WaterReport_v1
AS
SELECT s.ProjectId, s.PeriodKey, r.RowNo, r.ColumnCode,
       r.ValueString, r.ValueNumeric, r.ValueDate,
       s.BuiltAt, s.Status
FROM rpt.ReportSnapshot s
JOIN rpt.ReportRow      r ON r.SnapshotId = s.Id
JOIN rpt.ReportVersion  v ON v.Id = s.ReportVersionId
JOIN rpt.ReportDef      d ON d.Id = v.ReportDefId
WHERE d.Code = N'WaterReport'
  AND s.IsCurrent = 1
  AND s.Status IN (1, 2);   -- Approved, Submitted
-- ⛔ Нумерація ЗРІЗУ (`rpt.ReportSnapshot.Status` = SnapshotStatus, D-65):
-- 0 Draft, 1 Approved, 2 Submitted. Вона НЕ збігається з нумерацією подання
-- аркуша нижче (`wf.ApprovalState.Status` = DocumentStatus: 0 Draft,
-- 1 Submitted, 2 Approved, 3 Rejected): одиниця тут — Approved, там — Submitted.
-- Обидві фіксує `tests/Ecr.Architecture.Tests/StatusNumberingTests.cs`.
GO


-- ══ Шар СИРИХ даних документів для SSRS ════════════════════════════════════
-- Рішення людини 2026-09-30: «для SSRS ми маємо просто підготувати сирі дані
-- на основі яких він буде формувати звіти сам». Отже тут — лише дані
-- документів як вони є, без агрегацій і без звітної логіки; групує, рахує й
-- фільтрує SSRS. Два види вʼюх:
--   rpt.v_DocumentCells — одна «довга» вʼюха: комірка = рядок, усі шаблони;
--   rpt.v_<Шаблон>_<Аркуш>_<Таблиця>_v<Версія> — «широка» вʼюха на кожну
--     таблицю опублікованої версії шаблону: рядок таблиці = рядок вʼюхи,
--     колонка шаблону = колонка вʼюхи з її КОДОМ (генерує процедура нижче).
--
-- ⚠ Статус подання аркуша (`Status`: 0 Draft, 1 Submitted, 2 Approved,
-- 3 Rejected; немає стану — 0) — КОЛОНКА, а не фільтр: сирі дані віддаються
-- всі, а звіт регулятору обирає `Status = 2` сам. Це свідоме відхилення від
-- «фільтр у вʼюсі» (ФВ-10.11), яке стосувалося зрізів rpt.ReportSnapshot, —
-- їхня вʼюха вище фільтр тримає.
--
-- ⚠ Лише ЖИВІ дані `doc.*`: роки, винесені в архів (`arc.usp_ArchiveYear`),
-- сюди не потрапляють.
CREATE OR ALTER VIEW rpt.v_DocumentCells
AS
SELECT p.Id AS ProjectId, p.Code AS ProjectCode,
       t.Code AS TemplateCode, tv.[Version] AS TemplateVersion,
       d.Id AS DocumentId, d.BusinessKey AS DocumentKey,
       cv.PeriodKey,
       sd.Code AS SheetCode, td.Code AS TableCode,
       r.Id AS RowId, r.RowKey, r.Ordinal AS RowOrdinal,
       cd.Code AS ColumnCode, cd.DataType,
       cv.ValueString, cv.ValueNumeric, cv.ValueDate, cv.ValueBool,
       cv.ValueRegistryEntryId, cv.ValueUnitId, vu.Code AS ValueUnitCode,
       cu.Code AS ColumnUnitCode,
       cv.IsCalculated,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS Status,
       -- ⛔ RS (копії аркушів, людина 03.11): SheetCopyOrdinal — номер у групі (1 — оригінал, 2… — копія); SheetDisplayName —
       -- «<Назва> N»: назва групи (в документі або з шаблону, мова en, інакше код аркуша), з номером лише коли в групі є
       -- копії. Стан подання й дані копії — окремі від оригінала (SheetCopyId на екземплярі й на стані).
       CAST(COALESCE(dsh.CopyOrdinal, 1) AS smallint) AS SheetCopyOrdinal,
       (SELECT CONCAT(COALESCE(JSON_VALUE(MAX(CASE WHEN g.CopyOfId IS NULL THEN g.NameOverrideL10n END), N'$.en'),
                               JSON_VALUE(sd.NameL10n, N'$.en'), sd.Code),
                      CASE WHEN ti.SheetCopyId IS NOT NULL OR MAX(CASE WHEN g.CopyOfId IS NOT NULL THEN 1 ELSE 0 END) = 1
                           THEN CONCAT(N' ', COALESCE(dsh.CopyOrdinal, 1)) ELSE N'' END)
        FROM doc.DocumentSheet AS g WHERE g.DocumentId = d.Id AND g.SheetDefId = sd.Id) AS SheetDisplayName
FROM doc.CellValue          AS cv
JOIN doc.TableRow           AS r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0
JOIN doc.TableInstance      AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
JOIN doc.Document           AS d  ON d.Id = ti.DocumentId
JOIN doc.Project            AS p  ON p.Id = d.ProjectId
JOIN cfg.ColumnDef          AS cd ON cd.Id = cv.ColumnDefId
JOIN cfg.TableDef           AS td ON td.Id = ti.TableDefId
JOIN cfg.SheetDef           AS sd ON sd.Id = td.SheetDefId
JOIN cfg.TemplateVersion    AS tv ON tv.Id = sd.TemplateVersionId
JOIN cfg.Template           AS t  ON t.Id = tv.TemplateId
LEFT JOIN uom.Unit          AS vu ON vu.Id = cv.ValueUnitId
LEFT JOIN uom.Unit          AS cu ON cu.Id = cd.UnitId
LEFT JOIN wf.ApprovalState  AS a  ON a.DocumentId = d.Id AND a.SheetDefId = sd.Id AND a.PeriodKey = cv.PeriodKey
                                 AND (a.SheetCopyId = ti.SheetCopyId OR (a.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
LEFT JOIN doc.DocumentSheet AS dsh ON dsh.Id = ti.SheetCopyId
WHERE cv.IsEmpty = 0;
GO

-- Q-17.slotmap (P3, ON <= 1,2x OFF): READ MODEL слотів вʼюх із маршрутизацією місяців (@MonthRouting = 1). Рядок = (вʼюха,
-- ColumnDefId, слот n колонки вʼюхи, Mo): Mo NULL - колонка НЕ маршрутизується (власна комірка), 0 - введена не-місячна
-- (власник - якір року), 1..12 - місячна (власник - екземпляр цього місяця). Id у кількох кодах (аліаси cfg.ColumnRename)
-- = кілька рядків, кожен у своєму слоті. Таблицю веде ЛИШЕ генератор (обидві процедури нижче, EXECUTE AS OWNER), у тій самій
-- транзакції, що й ALTER VIEW; вʼюхи ON читають її замість inline-мапи VALUES і замість IN-списків/CASE за ColumnDefId
-- (умовна агрегація за слотом m.Slot, apply sc і похідна fam - лише по рядках мапи з Mo IS NOT NULL). Вʼюхи OFF її НЕ читають
-- (текст OFF байт-у-байт як раніше), а їхні рядки генератор прибирає. Індекс - кластерний ключ (ViewName, ColumnDefId, Slot):
-- seek по (вʼюха, колонка комірки) з apply на рядок і діапазон вʼюхи для хеш-зʼєднання fam.
IF OBJECT_ID(N'rpt.ViewSlot', N'U') IS NULL
    CREATE TABLE rpt.ViewSlot (
        ViewName    sysname NOT NULL,
        ColumnDefId int     NOT NULL,
        Slot        int     NOT NULL,
        Mo          int     NULL,
        CONSTRAINT PK_ViewSlot PRIMARY KEY CLUSTERED (ViewName, ColumnDefId, Slot));
GO

-- AN-155d: відбиток УСІХ входів, від яких залежить текст вʼюх rpt.v_*_vN та стабільних (обидві процедури нижче):
--   * @MonthRouting і (D-474) cfg.TemplateVersion.Layout кожної опублікованої версії;
--   * визначення самих процедур і цієї функції (правка 05-rpt-views.sql => інший відбиток => перегенерація);
--   * остання застосована міграція EF (dbo.__EFMigrationsHistory) — зміна таблиць під вʼюхами => перегенерація;
--   * опубліковані/застарілі версії (Status 1,2): cfg.Template(Code), cfg.TemplateVersion(Id, TemplateId, Version);
--   * їхні cfg.SheetDef(Id, Code, англ. назва NameL10n, IsDeleted=0), cfg.TableDef(Id, Code, LayoutKind, IsDeleted=0),
--     cfg.ColumnDef(Id, Code, DataType, IsMonthColumn, MonthNumber, IsDeleted=0);
--   * cfg.ColumnRename(From, To) (аліаси стабільних вʼюх);
--   * по таблицях — чи є активна прив'язка cfg.CalculationBinding з предикатом MatchJson (змінює форму вʼюхи; решта прив'язки
--     читається під час запиту).
-- Чернетки (Status 0) у відбиток не входять: вʼюх вони не дають. Функція лише читає; викликається процедурами (EXECUTE AS OWNER).
CREATE OR ALTER FUNCTION rpt.fn_ViewsFingerprint (@MonthRouting bit)
RETURNS varchar(64)
AS
BEGIN
    DECLARE @s nvarchar(max) = CONCAT(
        N'mr=', CAST(COALESCE(@MonthRouting, 0) AS int),
        -- Остання міграція EF: міграція може змінити таблиці, з яких читають вʼюхи (doc.*, calc.*) — перегенерувати один раз.
        N';mig=', (SELECT MAX(h.MigrationId) FROM dbo.__EFMigrationsHistory AS h),
        N';defs=', CONVERT(varchar(64), HASHBYTES('SHA2_256', CONCAT(
            COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'rpt.usp_GenerateTemplateViews')), N''), N'|',
            COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'rpt.usp_GenerateStableViews')), N''), N'|',
            COALESCE(OBJECT_DEFINITION(OBJECT_ID(N'rpt.fn_ViewsFingerprint')), N''))), 2),
        -- D-474: розкладка версії (Layout = 1 - маршрутизація місяців лише для неї) входить у відбиток: зміна => перегенерація.
        N';tpl=', CAST((SELECT t.Id AS i, t.Code AS c, tv.Id AS v, tv.[Version] AS n, tv.Layout AS l
                          FROM cfg.TemplateVersion AS tv JOIN cfg.Template AS t ON t.Id = tv.TemplateId
                         WHERE tv.Status IN (1, 2) ORDER BY tv.Id FOR XML RAW(N'r'), TYPE) AS nvarchar(max)),
        N';sh=', CAST((SELECT sd.Id AS i, sd.TemplateVersionId AS v, sd.Code AS c, JSON_VALUE(sd.NameL10n, N'$.en') AS e
                         FROM cfg.SheetDef AS sd JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId
                        WHERE tv.Status IN (1, 2) AND sd.IsDeleted = 0 ORDER BY sd.Id FOR XML RAW(N'r'), TYPE) AS nvarchar(max)),
        N';tb=', CAST((SELECT td.Id AS i, td.SheetDefId AS s, td.Code AS c, td.LayoutKind AS l
                         FROM cfg.TableDef AS td JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
                         JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId
                        WHERE tv.Status IN (1, 2) AND sd.IsDeleted = 0 AND td.IsDeleted = 0 ORDER BY td.Id FOR XML RAW(N'r'), TYPE) AS nvarchar(max)),
        N';co=', CAST((SELECT cd.Id AS i, cd.TableDefId AS t, cd.Code AS c, cd.DataType AS d, cd.IsMonthColumn AS m, cd.MonthNumber AS n
                         FROM cfg.ColumnDef AS cd JOIN cfg.TableDef AS td ON td.Id = cd.TableDefId
                         JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
                         JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId
                        WHERE tv.Status IN (1, 2) AND sd.IsDeleted = 0 AND td.IsDeleted = 0 AND cd.IsDeleted = 0
                        ORDER BY cd.Id FOR XML RAW(N'r'), TYPE) AS nvarchar(max)),
        N';rn=', CAST((SELECT r.FromColumnDefId AS f, r.ToColumnDefId AS t
                         FROM cfg.ColumnRename AS r ORDER BY r.FromColumnDefId, r.ToColumnDefId FOR XML RAW(N'r'), TYPE) AS nvarchar(max)),
        N';bp=', CAST((SELECT cb.TableDefId AS t FROM cfg.CalculationBinding AS cb
                        WHERE cb.IsActive = 1 AND REPLACE(cb.MatchJson, CHAR(32), N'') <> N'{}'
                        GROUP BY cb.TableDefId ORDER BY cb.TableDefId FOR XML RAW(N'r'), TYPE) AS nvarchar(max)));
    RETURN CONVERT(varchar(64), HASHBYTES('SHA2_256', @s), 2);
END;
GO

-- ⛔ D-14/D-66: застосунок DDL НЕ виконує. Він лише ВИКЛИКАЄ процедуру (при
-- публікації версії шаблону — ПІСЛЯ коміту публікації, «найкращим зусиллям»:
-- збій не відкочує публікацію, а лягає в журнал і в /health/ready; і на
-- старті), а
-- CREATE VIEW робить вона від імені ВЛАСНИКА (`EXECUTE AS OWNER`): обліковому
-- запису застосунку досить EXECUTE. Той самий шлях, що й
-- `arc.usp_EnsurePartitions`. DBA може викликати й сам:
--     EXEC rpt.usp_GenerateTemplateViews;                        -- усі версії
--     EXEC rpt.usp_GenerateTemplateViews @TemplateVersionId = 7; -- одна
--
-- ⚠ Версійність (ФВ-10.12, D-53): ім'я несе версію шаблону, а опублікована
-- версія структурно незмінна (D-16). Нова версія — нові вʼюхи; старі
-- лишаються з тими самими колонками й не видаляються ніколи.
--
-- ⚠ Ідемпотентна: текст вʼюхи — чиста функція структури версії; якщо в базі
-- вже рівно такий, ALTER не виконується (без Sch-M посеред читання SSRS).
--
-- ⚠ Службові колонки починаються з `_` — код колонки шаблону (`EcrCode`)
-- починається з літери, тож зіткнутися з ними не може. Колонки шаблону
-- унікальні в таблиці без урахування регістру (`UQ_ColumnDef`).
--
-- Тип колонки — за `ColumnDef.DataType`: String → ValueString; Int, Decimal,
-- Formula, Calculated → ValueNumeric; Bool → ValueBool; Date → ValueDate;
-- Lookup → ValueRegistryEntryId; Unit → ValueUnitId. ⚠ `Calculated` —
-- число методології з `calc.CalculationResult` (як у зрізі API; Q-17.recalc, див. нижче),
-- а коли результату немає, - те, що матеріалізовано в `doc.CellValue`.
-- AN-147: для Formula/Calculated додатково віддається колонка-супутник `<Код>_Text` (nvarchar): ValueString там, де
-- результат обчислення рядковий, тобто ПІДСУМКОВЕ числове значення колонки порожнє (для Calculated - з урахуванням
-- результату методології), інакше NULL. Числова колонка `<Код>` не змінюється.
-- ⛔ Q-17.7: @MonthRouting = 1 вмикає у вʼюхах MonthsInColumns читання значень з екземпляра ВЛАСНИКА (місячний M{m} - з
-- екземпляра періоду m, введений не-місячний - з якоря року; COALESCE із власною копією). Це те саме, що прапор
-- Collection:MonthColumnRouting (застосунок передає його сам - ReportViewGenerator); за замовчуванням 0, і текст вʼюх
-- БАЙТ-У-БАЙТ як до Q-17. DBA, що кличе процедуру руками на базі з увімкненим прапором, мусить передати @MonthRouting = 1:
--     EXEC rpt.usp_GenerateTemplateViews @MonthRouting = 1;
-- ✎ D-474: @MonthRouting - спадковий прапор «усі версії». Версія з cfg.TemplateVersion.Layout = 1 (YearWithMonthColumns)
-- маршрутизується і при @MonthRouting = 0 - рівно тим самим текстом, що й при прапорі ON. Layout = 0 - як до D-474.
-- AN-155d: @OnlyIfChanged = 1 (лише старт застосунку; публікація й DBA лишають 0 = завжди генерувати) пропускає ГЕНЕРАЦІЮ ВСІХ
-- версій (@TemplateVersionId IS NULL), якщо відбиток входів (rpt.fn_ViewsFingerprint) збігається з відбитком останньої
-- УСПІШНОЇ повної генерації (розширена властивість схеми rpt, без таблиці й міграції) і всі очікувані вʼюхи є в базі.
-- Пропуск повертає один рядок (0, N'', N'Skipped'). Відбиток пишеться ЛИШЕ після успішної повної генерації.
CREATE OR ALTER PROCEDURE rpt.usp_GenerateTemplateViews
    @TemplateVersionId int = NULL,
    @MonthRouting bit = 0,
    @OnlyIfChanged bit = 0
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Відбиток входів рахується ДО генерації: зміна шаблону посеред неї не загубиться (наступний старт побачить різницю).
    DECLARE @fp varchar(64) = rpt.fn_ViewsFingerprint(@MonthRouting);
    DECLARE @fpName sysname = N'ecr.views.fingerprint';

    DECLARE @result TABLE (TableDefId int NOT NULL, ViewName sysname NOT NULL, Action nvarchar(16) NOT NULL);
    DECLARE @tables TABLE (
        TableDefId        int NOT NULL PRIMARY KEY,
        SheetDefId        int NOT NULL,
        TemplateVersionId int NOT NULL,
        Header            nvarchar(400) NOT NULL,
        ViewName          nvarchar(400) NOT NULL);

    -- ⚠ Імена рахуються для ВСІХ опублікованих версій, а не лише для заданої:
    -- зіткнення можливе й між різними шаблонами (шаблон `A_B` аркуш `C` і
    -- шаблон `A` аркуш `B_C` дають одне ім'я).
    INSERT INTO @tables (TableDefId, SheetDefId, TemplateVersionId, Header, ViewName)
    SELECT td.Id, sd.Id, tv.Id,
           CONCAT(N'шаблон ', t.Code, N', версія ', tv.[Version], N', аркуш ', sd.Code, N', таблиця ', td.Code),
           CONCAT(N'v_', t.Code, N'_', sd.Code, N'_', td.Code, N'_v', tv.[Version])
      FROM cfg.TableDef        AS td
      JOIN cfg.SheetDef        AS sd ON sd.Id = td.SheetDefId
      JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId
      JOIN cfg.Template        AS t  ON t.Id = tv.TemplateId
     WHERE tv.Status IN (1, 2)   -- Published, Deprecated: застаріла версія вʼюх НЕ втрачає
       AND td.IsDeleted = 0 AND sd.IsDeleted = 0;

    -- Усе, що не [A-Za-z0-9_], стає `_` ("1.0.0.0" → "1_0_0_0"); CR/LF у
    -- заголовку-коментарі — пробілами.
    DECLARE @id int, @raw nvarchar(400), @pos int;
    DECLARE cur_names CURSOR LOCAL FAST_FORWARD FOR SELECT TableDefId FROM @tables;
    OPEN cur_names;
    FETCH NEXT FROM cur_names INTO @id;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @raw = ViewName FROM @tables WHERE TableDefId = @id;
        SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        WHILE @pos > 0
        BEGIN
            SET @raw = STUFF(@raw, @pos, 1, N'_');
            SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        END

        -- ⚠ Ім'я об'єкта — не довше 128. Довше скорочується детерміновано:
        -- початок + 12 знаків SHA-256 повного імені, тож те саме ім'я
        -- виходить у кожному середовищі й на кожному прогоні.
        IF LEN(@raw) > 128
            SET @raw = LEFT(@raw, 115) + N'_'
                     + LEFT(CONVERT(varchar(64), HASHBYTES('SHA2_256', @raw), 2), 12);

        UPDATE @tables
           SET ViewName = @raw,
               Header = REPLACE(REPLACE(Header, NCHAR(13), N' '), NCHAR(10), N' ')
         WHERE TableDefId = @id;
        FETCH NEXT FROM cur_names INTO @id;
    END
    CLOSE cur_names;
    DEALLOCATE cur_names;

    DELETE FROM @tables
     WHERE @TemplateVersionId IS NOT NULL AND TemplateVersionId <> @TemplateVersionId
       AND ViewName NOT IN (SELECT t2.ViewName FROM @tables AS t2 WHERE t2.TemplateVersionId = @TemplateVersionId);

    -- ⛔ Дві таблиці з одним ім'ям вʼюхи переписували б одна одній вʼюху по
    -- черзі — SSRS читав би то одну, то іншу. Жодна з них вʼюху не отримує,
    -- решта генерується, а наприкінці — помилка з іменем. Порівняння —
    -- зіставленням бази, тобто так само, як сервер порівнює імена об'єктів.
    DECLARE @clash nvarchar(400) = (SELECT TOP (1) ViewName FROM @tables GROUP BY ViewName HAVING COUNT(*) > 1);
    -- Перелік заголовків зіткнених таблиць — у повідомлення (для журналу й health).
    DECLARE @clashWho nvarchar(1500) = STUFF((
        SELECT N'; ' + t2.Header FROM @tables AS t2 WHERE t2.ViewName = @clash ORDER BY t2.TableDefId
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    DELETE FROM @tables
     WHERE ViewName IN (SELECT t2.ViewName FROM @tables AS t2 GROUP BY t2.ViewName HAVING COUNT(*) > 1);

    DELETE FROM @tables WHERE @TemplateVersionId IS NOT NULL AND TemplateVersionId <> @TemplateVersionId;

    -- AN-155d: пропуск, якщо входи не змінилися з останньої успішної повної генерації й усі вʼюхи на місці.
    IF @OnlyIfChanged = 1 AND @TemplateVersionId IS NULL
       AND @fp = (SELECT CONVERT(varchar(64), ep.value) FROM sys.extended_properties AS ep
                   WHERE ep.class = 3 AND ep.major_id = SCHEMA_ID(N'rpt') AND ep.minor_id = 0 AND ep.name = @fpName)
       AND NOT EXISTS (SELECT 1 FROM @tables AS tt WHERE OBJECT_ID(N'rpt.' + QUOTENAME(tt.ViewName), N'V') IS NULL)
       -- Q-17.slotmap: вʼюха, що читає мапу слотів, має свої рядки в rpt.ViewSlot (інакше - перегенерувати, а не пропустити).
       AND NOT EXISTS (SELECT 1 FROM @tables AS tt
                         JOIN sys.sql_expression_dependencies AS dep
                           ON dep.referencing_id = OBJECT_ID(N'rpt.' + QUOTENAME(tt.ViewName), N'V') AND dep.referenced_id = OBJECT_ID(N'rpt.ViewSlot', N'U')
                        WHERE NOT EXISTS (SELECT 1 FROM rpt.ViewSlot AS vs WHERE vs.ViewName = tt.ViewName))
    BEGIN
        SELECT CAST(0 AS int) AS TableDefId, CAST(N'' AS nvarchar(400)) AS ViewName, CAST(N'Skipped' AS nvarchar(16)) AS Action;
        RETURN;
    END

    DECLARE @sheet int, @header nvarchar(400), @view sysname;
    DECLARE @select nvarchar(max), @selectText nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
    DECLARE @colId int, @colCode nvarchar(64), @colType tinyint, @alias nvarchar(16), @n int;
    -- Q-17.7: власність значень MonthsInColumns (місячний стовпець M{m} — в екземплярі періоду m,
    -- введений не-місячний — в екземплярі-якорі року). @monthMask: біт m — потрібен власник-місяць m.
    DECLARE @layout tinyint, @isMonth bit, @monthNo tinyint, @isInput bit, @monthMask int, @needAnchor bit,
            @ownerRows nvarchar(max), @ownerCells nvarchar(max), @valueCol nvarchar(128), @extra int, @mi int;
    -- Q-17.fix (B1): умовні агрегації значень власників - по одному OUTER APPLY на власника (OwnerNo: -1 власна копія,
    -- 0 якір року, 1..12 місяць), а не по приєднанню doc.CellValue на колонку.
    DECLARE @routed bit, @raw1 nvarchar(64), @agg nvarchar(max), @aggBody nvarchar(max);
    DECLARE @marker nvarchar(64) = N'-- Згенеровано rpt.usp_GenerateTemplateViews';
    DECLARE @failure nvarchar(2048);
    -- Q-17.recalc: результати методологій для колонок Calculated (див. нижче). @selectRv - той самий список колонок, але
    -- Calculated читається з результатів; @rvIds / @rvAgg - колонки й їхні агрегати; @rvApply - сам OUTER APPLY.
    DECLARE @selectRv nvarchar(max), @valueColRv nvarchar(256), @rvIds nvarchar(max), @rvAgg nvarchar(max), @rvApply nvarchar(max), @selectTextRv nvarchar(max);
    -- Q-17.perf (бюджети Land, рішення людини 28.10): rv - НАБОРОВО (дві похідні таблиці по (документ, період, RowKey) замість
    -- OUTER APPLY на рядок з ~45 seek-ів); @rvPack - агрегати пакетів у похідних, @rvExprs - вибір «власний > загальний» у
    -- скалярному apply rv; @rvPred - є прив'язка з предикатом MatchJson (тоді лишається посередній apply на рядок).
    -- @plainAgg / @plainCnt - при прапорі ON усі НЕмаршрутизовані колонки читає ОДИН apply pc замість c<n> на колонку.
    DECLARE @rvPack nvarchar(max), @rvChoose nvarchar(max), @rvExprs nvarchar(max), @rvPred bit, @rvDer nvarchar(max), @rvOwn nvarchar(max), @rvGen nvarchar(max),
            @plainAgg nvarchar(max), @plainCnt int, @numRef nvarchar(64), @strRef nvarchar(64),
            @famAgg nvarchar(max), @pkExpr nvarchar(16), @rvRef nvarchar(8), @fromSql nvarchar(max), @tiAp nvarchar(max), @rowApply nvarchar(max);
    -- RS (копії аркушів): службові колонки _SheetCopyOrdinal/_SheetDisplayName — у КІНЦІ списку (адитивні, як <Код>_Text).
    DECLARE @sheetNameLit nvarchar(max), @copyCols nvarchar(max);
    -- Q-17.slotmap: агрегати за слотом (pc / sc), літерал імені вʼюхи, бажані рядки rpt.ViewSlot і чи їх треба переписати.
    DECLARE @plainAggS nvarchar(max), @scAggS nvarchar(max), @vnLit nvarchar(300), @useSlots bit, @mapDiff bit, @ddl bit;
    DECLARE @slot TABLE (ColumnDefId int NOT NULL, Slot int NOT NULL, Mo int NULL, PRIMARY KEY (ColumnDefId, Slot));
    -- D-474: маршрутизація місяців версії поточної вʼюхи (@MonthRouting = 1 або cfg.TemplateVersion.Layout = 1).
    DECLARE @mr bit;

    DECLARE cur_views CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableDefId, SheetDefId, Header, ViewName FROM @tables ORDER BY TableDefId;
    OPEN cur_views;
    FETCH NEXT FROM cur_views INTO @id, @sheet, @header, @view;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- ⚠ Цикл, а не `STRING_AGG`: підлога сервера — 2016 SP1 (`D-101`).
        SELECT @select = N'', @selectText = N'', @joins = N'', @n = 0,
               @ownerRows = N'', @ownerCells = N'', @monthMask = 0, @needAnchor = 0, @extra = 0,
               @selectRv = N'', @rvIds = N'', @rvAgg = N'', @rvApply = N'', @selectTextRv = N'',
               @rvPack = N'', @rvChoose = N'', @rvExprs = N'', @plainAgg = N'', @plainCnt = 0,
               @plainAggS = N'', @scAggS = N'', @vnLit = N'N''' + REPLACE(@view, N'''', N'''''') + N'''';
        DELETE FROM @slot;
        SET @layout = (SELECT LayoutKind FROM cfg.TableDef WHERE Id = @id);
        -- D-474: маршрутизація місяців ДЛЯ ЦІЄЇ ВЕРСІЇ - прапор (усі версії) або розкладка версії YearWithMonthColumns (1).
        -- Прапор ON або Layout = 0 - текст вʼюхи рівно той самий, що до D-474.
        SET @mr = CASE WHEN @MonthRouting = 1
                         OR EXISTS (SELECT 1 FROM cfg.SheetDef AS lsd JOIN cfg.TemplateVersion AS ltv ON ltv.Id = lsd.TemplateVersionId
                                     WHERE lsd.Id = @sheet AND ltv.Layout = 1) THEN 1 ELSE 0 END;
        -- Q-17.perf: чи є в таблиці активна прив'язка з предикатом MatchJson (тоді rv - посередній apply на рядок, нижче).
        -- rv наборового варіанта стає колонками apply-джерела рядків r (alias r), посередній - окремим apply rv.
        SET @rvPred = CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding
                                         WHERE IsActive = 1 AND TableDefId = @id AND REPLACE(MatchJson, CHAR(32), N'') <> N'{}') THEN 1 ELSE 0 END;
        SET @rvRef = CASE WHEN @rvPred = 0 THEN N'r' ELSE N'rv' END;
        SELECT @fromSql = N'', @tiAp = N'', @rowApply = N'', @famAgg = N'';
        SET @colId = (SELECT MIN(Id) FROM cfg.ColumnDef WHERE TableDefId = @id AND IsDeleted = 0);
        WHILE @colId IS NOT NULL
        BEGIN
            SELECT @colCode = Code, @colType = DataType, @isMonth = IsMonthColumn, @monthNo = MonthNumber
              FROM cfg.ColumnDef WHERE Id = @colId;
            SET @n = @n + 1;
            SET @alias = N'c' + CAST(@n AS nvarchar(10));
            SET @valueCol = @alias + N'.'
                + CASE @colType
                      WHEN 0 THEN N'ValueString'
                      WHEN 3 THEN N'ValueBool'
                      WHEN 4 THEN N'ValueDate'
                      WHEN 5 THEN N'ValueRegistryEntryId'
                      WHEN 7 THEN N'ValueUnitId'
                      ELSE N'ValueNumeric'
                  END;
            -- Q-17.7: MonthsInColumns — ВВЕДЕНИЙ стовпець (не Formula/Calculated) читається з комірки власника;
            -- власна копія в рядку перегляду лишається запасним значенням (до консолідації даних і після неї
            -- до -PurgeCopies). Похідні (Formula/Calculated) і всі інші розкладки — як було.
            SET @routed = CASE WHEN @mr = 1 AND @layout = 0 AND @colType NOT IN (6, 8)
                                    AND (@isMonth = 0 OR @monthNo BETWEEN 1 AND 12) THEN 1 ELSE 0 END;

            SET @numRef = @alias + N'.ValueNumeric';
            SET @strRef = @alias + N'.ValueString';
            IF @routed = 0 AND @mr = 1
            BEGIN
                -- Q-17.perf (T3_00 Land: 232 колонки = 232 LEFT JOIN doc.CellValue = 233 seek-ів на рядок, 5,4 млн виконань): при прапорі ON
                -- усі НЕмаршрутизовані колонки (PerPeriodInstance/Static, а в MonthsInColumns - Formula/Calculated) читає ОДИН
                -- apply pc по ключу рядка (PeriodKey, TableRowId) з умовною агрегацією; ключ комірки унікальний, тож MAX = саме значення.
                -- Прапор вимкнено - текст вʼюхи як раніше (c<n>).
                SET @raw1 = SUBSTRING(@valueCol, LEN(@alias) + 2, 64);
                SET @plainAgg = @plainAgg + N', '
                    + CASE WHEN @colType = 3
                           THEN N'MAX(CAST(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                           ELSE N'MAX(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));
                SET @numRef = N'pc.v' + CAST(@n AS nvarchar(10));
                SET @strRef = N'pc.s' + CAST(@n AS nvarchar(10));
                IF @colType IN (6, 8)
                    SET @plainAgg = @plainAgg + N', MAX(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN x.ValueString END) AS s'
                                  + CAST(@n AS nvarchar(10));
                -- Q-17.slotmap: той самий агрегат за слотом (m.Slot) - для вʼюхи з маршрутизованими колонками (вибір нижче).
                SET @plainAggS = @plainAggS + N', '
                    + CASE WHEN @colType = 3
                           THEN N'MAX(CAST(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                           ELSE N'MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10))
                    + CASE WHEN @colType IN (6, 8)
                           THEN N', MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.ValueString END) AS s' + CAST(@n AS nvarchar(10))
                           ELSE N'' END;
                INSERT INTO @slot (ColumnDefId, Slot, Mo) VALUES (@colId, @n, NULL);
                SET @plainCnt = @plainCnt + 1;
                SET @valueCol = CASE WHEN @colType = 3 THEN N'CAST(pc.v' + CAST(@n AS nvarchar(10)) + N' AS bit)'
                                     ELSE N'pc.v' + CAST(@n AS nvarchar(10)) END;
            END
            ELSE IF @routed = 0
            BEGIN
                SET @joins = @joins + N'
LEFT JOIN doc.CellValue AS ' + @alias + N' ON ' + @alias + N'.PeriodKey = r.PeriodKey AND '
                    + @alias + N'.TableRowId = r.Id AND ' + @alias + N'.ColumnDefId = ' + CAST(@colId AS nvarchar(10));
            END
            ELSE
            BEGIN
                -- ⛔ Q-17.fix (B1): жодного приєднання doc.CellValue НА КОЛОНКУ - ліміт SQL Server 256 таблиць у запиті
                -- (T9_04 Land: 203 колонки давали 399 таблиць). Значення введених колонок читає ОДИН OUTER APPLY на
                -- кожного власника (власна копія; кожен із ≤ 12 місяців; якір): умовна агрегація MAX(CASE ColumnDefId)
                -- по ключу комірки (PeriodKey, TableRowId, ColumnDefId) унікальна, тож MAX = саме це значення.
                -- Тип значення той самий: bit читається як tinyint і повертається CAST(.. AS bit).
                SET @raw1 = SUBSTRING(@valueCol, LEN(@alias) + 2, 64);   -- ValueNumeric / ValueString / ...
                SET @aggBody = CASE WHEN @colType = 3
                                THEN N'MAX(CAST(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                                ELSE N'MAX(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)'
                           END;
                SET @agg = @aggBody + N' AS v' + CAST(@n AS nvarchar(10));
                -- Власник місячної колонки - екземпляр її місяця: ОКРЕМИЙ apply на кожен місяць-власник (P2-1, OwnerNo 100 + m),
                -- що читає лише екземпляр цього місяця й лише колонки цього місяця (seek по ключах). Один спільний apply
                -- на всі місяці давав ~12·C² операцій на рядок (340x повільніше за OFF на T9_04 Land), а окремі apply
                -- оптимізатор викидає, коли колонки їхнього місяця не вибрані.
                SET @mi = CASE WHEN @isMonth = 1 THEN 100 + @monthNo ELSE 0 END;
                IF @isMonth = 1
                    SET @monthMask = 1;
                ELSE
                    SET @needAnchor = 1;

                -- P2-1 (Audit-2 проход C, p61 -> viewfix): обчислена комірка (IsCalculated = 1) екземпляра перегляду, що НЕСЕ
                -- значення, - СВОЯ і приховує значення власника (так її віддає зріз API: MonthRoutedCellStore лишає обчислені,
                -- шаруючи власника лише над введеними). Порожня обчислена (IsEmpty, результат NULL) значення власника НЕ
                -- приховує. Правило - на рівні КОМІРКИ: значення = COALESCE(власна обчислена, власник, власна копія).
                -- Власна обчислена читається ОКРЕМИМ apply sc (OwnerNo -2) лише по обчислених комірках рядка (їх мало, а то й нема),
                -- а не другим агрегатом на колонку в sv: p61 (прапор k<n> у sv) подвоював роботу sv (T10_03 / T11_03 у 2-4 рази).
                -- Q-17.perf3 (Land T9_04 ON 2,2-2,4 с проти OFF 0,5-0,8 с): власна копія - у СПІЛЬНОМУ apply pc (ті самі рядки й той
                -- самий агрегат), а не окремим apply sv (зайвий seek і прохід на рядок, +~200 агрегатів у тексті й компіляції).
                SET @plainAgg = @plainAgg + N', ' + @agg;
                SET @plainCnt = @plainCnt + 1;
                -- Q-17.slotmap: pc і sc - за слотом (рядок мапи з Mo: місяць колонки або 0 = якір року).
                SET @plainAggS = @plainAggS + N', ' + CASE WHEN @colType = 3
                    THEN N'MAX(CAST(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                    ELSE N'MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));
                SET @scAggS = @scAggS + N', ' + CASE WHEN @colType = 3
                    THEN N'MAX(CAST(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                    ELSE N'MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS cv' + CAST(@n AS nvarchar(10));
                INSERT INTO @slot (ColumnDefId, Slot, Mo) VALUES (@colId, @n, CASE WHEN @isMonth = 1 THEN @monthNo ELSE 0 END);
                -- Q-17.perf3: fam агрегував УСІ комірки 12 екземплярів року (45 520 рядків на T9_04 Land, 1,96 с CPU з 3,0) через
                -- ~200 CASE (ColumnDefId + місяць/якір) на кожну. Тепер у агрегацію йдуть лише комірки, що НАЛЕЖАТЬ власнику (мапа fm
                -- (ColumnDefId, Mo, n): Mo - місяць колонки або 0 = якір року; хеш-зʼєднання), а колонку вибирає номер слота fm.n.
                -- Значення ті самі: решта комірок у старому тексті давала NULL у кожен агрегат; MAX нечутливий до дублікатів.
                SET @famAgg = @famAgg + N', ' + CASE WHEN @colType = 3
                    THEN N'MAX(CAST(CASE WHEN fm.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN c.' + @raw1 + N' END AS tinyint))'
                    ELSE N'MAX(CASE WHEN fm.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN c.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));

                SET @valueCol = N'COALESCE(sc.cv' + CAST(@n AS nvarchar(10)) + N', '
                              + N'fam.v' + CAST(@n AS nvarchar(10)) + N', pc.v' + CAST(@n AS nvarchar(10)) + N')';
                IF @colType = 3
                    SET @valueCol = N'CAST(' + @valueCol + N' AS bit)';
            END

            -- Q-17.recalc: колонка Calculated (8) у зрізі API - число методології (CalculatedCellOverlay, calc.CalculationResult),
            -- а не doc.CellValue. Вʼюха бере його з OUTER APPLY rv (нижче), а матеріалізоване значення лишає запасним.
            SET @valueColRv = @valueCol;
            IF @colType = 8
            BEGIN
                SET @valueColRv = N'COALESCE(' + @rvRef + N'.rv' + CAST(@n AS nvarchar(10)) + N', ' + @valueCol + N')';
                SET @rvIds = @rvIds + N', ' + CAST(@colId AS nvarchar(10));
                -- Переможець - прив'язка з найбільшим Id: рядок "Id (10 цифр) + значення або N" береться через MAX і розпаковується.
                SET @rvAgg = @rvAgg + N', CAST(NULLIF(SUBSTRING(MAX(CASE WHEN q.ColumnDefId = ' + CAST(@colId AS nvarchar(10))
                           + N' THEN q.Pack END), 11, 60), ''N'') AS decimal(34, 16)) AS rv' + CAST(@n AS nvarchar(10));
                -- Q-17.perf: наборовий варіант - пакет колонки з похідних ro (результат екземпляра цього рядка) і rg (загальний);
                -- «власний > загальний» ПО ПРИВ'ЯЗЦІ: переможець - найбільший Id прив'язки серед обох, при рівному - власний.
                SET @rvPack = @rvPack + N', MAX(CASE WHEN q.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' THEN q.Pack END) AS p' + CAST(@n AS nvarchar(10));
                -- Пакет - двійковий (4 Б Id прив'язки + 1 Б «є число» + 20 Б decimal(34,16)): рядки хеш-таблиці не мають перевищити 8060 Б
                -- (nvarchar-пакет x 40 колонок Calculated давав "worktable ... exceeds 8060").
                SET @rvExprs = @rvExprs + N', CASE WHEN SUBSTRING(rvx.p' + CAST(@n AS nvarchar(10)) + N', 5, 1) = 0x01 THEN CAST(SUBSTRING(rvx.p'
                             + CAST(@n AS nvarchar(10)) + N', 6, 20) AS decimal(34, 16)) END AS rv' + CAST(@n AS nvarchar(10));
            END

            SET @select = @select + N',
       ' + @valueCol + N' AS ' + QUOTENAME(@colCode);
            -- AN-147: колонка-супутник `<Код>_Text` лише для Formula/Calculated - РЯДКОВИЙ результат обчислення (ValueString);
            -- числова колонка вище не змінюється. Формула/Calculated ніколи не маршрутизується (@routed = 0), тож і в режимі
            -- @MonthRouting = 1 супутник читає ту саму власну комірку c<n> (власна обчислена > власник дотримано). Супутники
            -- ідуть У КІНЦІ списку колонок (позиції наявних колонок не зсуваються). Колізія імені: якщо в таблиці вже є
            -- колонка з кодом `<Код>_Text`, супутник для цієї колонки ПРОПУСКАЄТЬСЯ (наявна колонка не підміняється).
            IF @colType IN (6, 8)
               AND NOT EXISTS (SELECT 1 FROM cfg.ColumnDef
                                WHERE TableDefId = @id AND IsDeleted = 0 AND Code = @colCode + N'_Text')
                SET @selectText = @selectText + N',
       CASE WHEN ' + @numRef + N' IS NULL THEN ' + @strRef + N' END AS '
                                + QUOTENAME(@colCode + N'_Text');
            -- Варіант з rv: для Calculated супутник заповнюється ЛИШЕ коли ПІДСУМКОВЕ число (результат методології, інакше
            -- матеріалізоване - те, що віддає числова колонка) порожнє; методологія дала число - супутник NULL.
            IF @colType IN (6, 8)
               AND NOT EXISTS (SELECT 1 FROM cfg.ColumnDef
                                WHERE TableDefId = @id AND IsDeleted = 0 AND Code = @colCode + N'_Text')
                SET @selectTextRv = @selectTextRv + N',
       CASE WHEN ' + CASE WHEN @colType = 8 THEN N'COALESCE(' + @rvRef + N'.rv' + CAST(@n AS nvarchar(10)) + N', ' + @numRef + N')'
                          ELSE @numRef END
                                + N' IS NULL THEN ' + @strRef + N' END AS '
                                + QUOTENAME(@colCode + N'_Text');
            SET @selectRv = @selectRv + N',
       ' + @valueColRv + N' AS ' + QUOTENAME(@colCode);
            SET @colId = (SELECT MIN(Id) FROM cfg.ColumnDef WHERE TableDefId = @id AND IsDeleted = 0 AND Id > @colId);
        END
        SET @select = @select + @selectText;
        SET @selectRv = @selectRv + @selectTextRv;   -- AN-147: супутники й у варіанті з rv (Calculated числом методології)

        -- Q-17.7: приєднання власників — по одному екземпляру/рядку на потрібний місяць і на якір.
        -- Усі з'єднання — по ключах: UQ_TableInstance (PeriodKey, DocumentId, TableDefId) і UQ_TableRow_Key
        -- (PeriodKey, TableInstanceId, RowKey), обидва вирівняні по ps_ByPeriodKey; без підзапитів на рядок
        -- (якір — похідна таблиця по doc.Period, обчислюється один раз на запит). Лише Monthly-проєкти:
        -- у квартальних/річних ключ Year*100+Sequence означає не місяць.
        -- Q-17.slotmap: вʼюха з маршрутизованими колонками читає мапу слотів rpt.ViewSlot (pc, sc, fam); решта - як була.
        SET @useSlots = CASE WHEN @monthMask <> 0 OR @needAnchor = 1 THEN 1 ELSE 0 END;
        IF @plainCnt > 0
        BEGIN
            SET @joins = @joins + CASE WHEN @useSlots = 1 THEN N'
OUTER APPLY (SELECT ' + STUFF(@plainAggS, 1, 2, N'') + N' FROM doc.CellValue AS x INNER LOOP JOIN rpt.ViewSlot AS m ON m.ViewName = ' + @vnLit
                + N' AND m.ColumnDefId = x.ColumnDefId WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id) AS pc' ELSE N'
OUTER APPLY (SELECT ' + STUFF(@plainAgg, 1, 2, N'') + N' FROM doc.CellValue AS x WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id) AS pc' END;
            SET @extra = @extra + 1 - @plainCnt + @useSlots;   -- pc замість c<n> на кожну нероутовану колонку (+ мапа слотів)
        END

        IF @monthMask <> 0 OR @needAnchor = 1
        BEGIN
            -- Q-17.perf2 (Аудит-2 на 24147ebd4; синтетика за формою Land, 480 екземплярів): власники читалися ланцюжком НА РЯДОК (ow<m> -
            -- seek TableRow + om<m> - діапазон CellValue, x12 місяців + якір = ~100 читань і ~26 seek-ів TableRow на рядок), причому той самий
            -- рядок власника перечитувався для кожного з 12 періодів родини. Тепер значення власників (місячні - з екземпляра свого місяця,
            -- введені не-місячні - з якоря року) збираються ОДИН раз НАБОРОМ по (документ, рік, RowKey): один прохід
            -- doc.TableInstance -> doc.TableRow -> doc.CellValue таблиці з умовною агрегацією (MAX(CASE ColumnDefId і місяць/якір)) і
            -- приєднуються до рядків хеш-зʼєднанням (ліворуч лише ti, r, d, p, a - вузький потік). Семантика та сама: немає екземпляра/
            -- рядка власника/комірки - NULL; PeriodKind <> 0 - так само NULL (умова в ON).
            SET @extra = @extra + 6;   -- oi, od, pa (doc.Period), ow, x у похідній + її доступ
            -- Q-17.perf3: у агрегацію йдуть лише комірки власників (мапа fm, хеш-зʼєднання; умова Mo > 0 - місяць екземпляра,
            -- Mo = 0 - якір року), колонку вибирає слот fm.n.
            -- Q-17.slotmap: мапа fm - рядки rpt.ViewSlot цієї вʼюхи з Mo IS NOT NULL (замість inline VALUES; та сама умова власника).
            SET @extra = @extra + 1;   -- fm (rpt.ViewSlot)
            SET @ownerRows = N'
LEFT HASH JOIN (SELECT c.DocumentId, c.Yr, c.RowKey' + @famAgg + N'
                  FROM rpt.ViewSlot AS fm
                  INNER HASH JOIN (SELECT oi.DocumentId, oi.PeriodKey / 100 AS Yr, oi.PeriodKey, pa.AnchorKey, ow.RowKey, x.ColumnDefId,
                                          x.ValueString, x.ValueNumeric, x.ValueDate, x.ValueBool, x.ValueRegistryEntryId, x.ValueUnitId
                                     FROM doc.TableInstance AS oi
                                     JOIN doc.Document AS od ON od.Id = oi.DocumentId
                                     LEFT JOIN (SELECT ProjectId, PeriodKey / 100 AS Yr, MIN(PeriodKey) AS AnchorKey
                                                  FROM doc.Period GROUP BY ProjectId, PeriodKey / 100) AS pa
                                            ON pa.ProjectId = od.ProjectId AND pa.Yr = oi.PeriodKey / 100
                                     JOIN doc.TableRow AS ow ON ow.PeriodKey = oi.PeriodKey AND ow.TableInstanceId = oi.Id AND ow.IsDeleted = 0
                                     JOIN doc.CellValue AS x ON x.PeriodKey = ow.PeriodKey AND x.TableRowId = ow.Id
                                    WHERE oi.TableDefId = ' + CAST(@id AS nvarchar(10)) + N') AS c
                          ON c.ColumnDefId = fm.ColumnDefId
                         AND ((fm.Mo > 0 AND c.PeriodKey % 100 = fm.Mo) OR (fm.Mo = 0 AND c.PeriodKey = c.AnchorKey))
                 WHERE fm.ViewName = ' + @vnLit + N' AND fm.Mo IS NOT NULL
                 GROUP BY c.DocumentId, c.Yr, c.RowKey) AS fam
       ON p.PeriodKind = 0 AND fam.DocumentId = ti.DocumentId AND fam.Yr = ti.PeriodKey / 100 AND fam.RowKey = r.RowKey';
            -- Власна обчислена (sc) - OUTER APPLY з умовною агрегацією (B1) лише по обчислених комірках МАРШРУТИЗОВАНИХ колонок
            -- (seek мапи по (вʼюха, ColumnDefId) з Mo IS NOT NULL): обчислені Formula/Calculated у агрегацію більше не йдуть
            -- (Land T9_04: 3 448 обчислених комірок екземпляра x ~150 CASE, із них маршрутизованих - 0). Власна копія - у pc.
            SET @extra = @extra + 2;   -- sc + його мапа
            SET @ownerCells = N'
OUTER APPLY (SELECT ' + STUFF(@scAggS, 1, 2, N'') + N' FROM doc.CellValue AS x INNER LOOP JOIN rpt.ViewSlot AS m ON m.ViewName = ' + @vnLit
                + N' AND m.ColumnDefId = x.ColumnDefId AND m.Mo IS NOT NULL WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id AND x.IsCalculated = 1) AS sc';

            -- Порядок: приєднання власників-рядків ідуть ДО комірок власників; власні копії нероутованих колонок (c<n>) - між ними.
            SET @joins = @ownerRows + @joins + @ownerCells;
        END

        -- ⛔ Q-17.recalc (Аудит-2, Land-заміри): зріз API віддає колонку Calculated числом методології
        -- (`CalculatedCellOverlay`: calc.CalculationResult за активною прив'язкою cfg.CalculationBinding), а doc.CellValue її
        -- не містить, тож вʼюха показувала NULL. Той самий відбір, що в зрізі, - ОДИН OUTER APPLY rv на рядок для всіх таких колонок:
        --   * результати АКТУАЛЬНИХ прогонів документа (ReadCurrentAsync: власний документний прогін має перевагу над
        --     проєктним, якщо він є для цього періоду) за документом, періодом екземпляра й RowKey рядка;
        --   * вихід = OutputCode прив'язки, версія - будь-яка версія методології прив'язки;
        --   * результат екземпляра цього рядка точніший за результат без екземпляра (D-401); решта екземплярів ігноруються;
        --   * предикат MatchJson (`{}` - уся таблиця; інакше всі пари "ColumnDefId": "текст" мусять збігтися з комірками
        --     рядка - MethodologyRuleMatcher);
        --   * кілька прив'язок на колонку - виграє найбільший Id; різні речовини сумуються, а розбіжні значення однієї
        --     речовини дають NULL (TryCombine).
        -- Прив'язки читаються під час запиту (не під час генерації), тож нова прив'язка діє без перегенерації вʼюх.
        -- Лише колонки Calculated; решта (введені, Formula) - як було. Матеріалізоване значення doc.CellValue лишається запасним.
        -- Ціна: +8 таблиць у запиті (лічильник нижче); не вміщається в ліміт - apply не додається, вʼюха як до Q-17.recalc.
        -- Q-17.perf (Аудит-2 p66, Land: T3_00 rv ~107 з 130 с; діагноз 002c19cbd): OUTER APPLY на РЯДОК x прив'язку читав
        -- IX_CalculationResult_Lookup (SourceRowKey лише residual) -> key lookup PK -> CalculationRun для КОЖНОГО результату, 88 % з них -
        -- Superseded-прогони (ціна росте з кожним перерахунком). Тепер результати збираються ОДИН раз на ЕКЗЕМПЛЯР (ведучий набір
        -- ti -> CROSS APPLY): спершу АКТУАЛЬНІ прогони (ru, UX_CalculationRun_Current), далі seek IX_CalculationResult_DocRun
        -- (PeriodKey, DocumentId, CalculationRunId) - як у CalculationResultStore.ReadCurrentAsync, - прив'язки таблиці, і групування
        -- за SourceRowKey; рядки таблиці приєднуються до цього набору хеш-зʼєднанням (лівий потік вузький: лише рядки екземпляра).
        -- Семантика та сама (власний екземпляр > загальний ПО ПРИВ'ЯЗЦІ, TryCombine, найбільший Id прив'язки, документний
        -- прогін > проєктний). Значення колонок Calculated - колонки rv<n> джерела рядків r. Предикат MatchJson залежить від комірок
        -- рядка й набором не виражається: якщо в таблиці Є активна прив'язка з предикатом (на момент генерації), лишається
        -- посередній apply на рядок нижче (ізольована гілка); прив'язка з предикатом, додана ПІСЛЯ генерації, набором ігнорується
        -- до перегенерації (старт застосунку і публікація шаблону перегенерують вʼюхи). Ціна: +7 таблиць у запиті.
        -- Q-17.rvshape: runs-driven read. A result lives in the partition of its RUN's period (CalculationResultStore: periodKey = run.PeriodKey),
        -- so runs are restricted to ti.PeriodKey and the join ru -> cr is forced to LOOP: the optimizer then seeks IX_CalculationResult_DocRun on all
        -- THREE key columns (PeriodKey, DocumentId, CalculationRunId) and reads only the rows of the Current runs. Without the hint the plan seeks
        -- (PeriodKey, DocumentId) and filters the run above the seek, i.e. reads every Superseded run of the document-period (Land: 4.47 M rows).
        IF @rvIds <> N'' AND @rvPred = 0 AND @n + @extra + 7 <= 250
        BEGIN
            SET @rowApply = N'
CROSS APPLY (SELECT r0.Id, r0.RowKey, r0.Ordinal, r0.PeriodKey' + @rvExprs + N'
             FROM doc.TableRow AS r0
             LEFT HASH JOIN (SELECT q.SourceRowKey' + @rvPack + N'
                  FROM (SELECT t.SourceRowKey, t.ColumnDefId,
                               CAST(t.BindingId AS binary(4)) + CAST(CASE WHEN t.Total IS NULL THEN 0 ELSE 1 END AS binary(1))
                               + CAST(COALESCE(t.Total, 0) AS binary(20)) AS Pack
                        FROM (SELECT g.SourceRowKey, g.BindingId, g.ColumnDefId,
                                     CASE WHEN MAX(g.Own) = 1
                                          THEN CASE WHEN MAX(CASE WHEN g.Own = 1 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 1 THEN g.SubTotal END) END
                                          ELSE CASE WHEN MAX(CASE WHEN g.Own = 0 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 0 THEN g.SubTotal END) END
                                     END AS Total
                              FROM (SELECT u.SourceRowKey, u.BindingId, u.ColumnDefId, u.Own, MIN(u.Value) AS SubTotal,
                                           CASE WHEN MIN(u.Value) <> MAX(u.Value) OR MIN(u.UnitId) <> MAX(u.UnitId) THEN 1 ELSE 0 END AS Amb
                                    FROM (SELECT cr.SourceRowKey, b.Id AS BindingId, b.ColumnDefId, cr.SubstanceEntryId, cr.Value, cr.UnitId,
                                                 CASE WHEN cr.TableInstanceId IS NULL THEN 0 ELSE 1 END AS Own
                                          FROM calc.CalculationRun AS ru
                                          INNER LOOP JOIN calc.CalculationResult AS cr ON cr.PeriodKey = ti.PeriodKey AND cr.DocumentId = d.Id AND cr.CalculationRunId = ru.Id
                                          INNER HASH JOIN (SELECT bb.Id, bb.ColumnDefId, bb.OutputCode, mv.Id AS MvId
                                                             FROM cfg.CalculationBinding AS bb
                                                             JOIN calc.MethodologyVersion AS mv ON mv.MethodologyId = bb.MethodologyId
                                                            WHERE bb.IsActive = 1 AND bb.TableDefId = ' + CAST(@id AS nvarchar(10)) + N' AND bb.ColumnDefId IN (' + STUFF(@rvIds, 1, 2, N'') + N')
                                                              AND REPLACE(bb.MatchJson, CHAR(32), N'''') = N''{}'') AS b
                                               ON b.MvId = cr.MethodologyVersionId AND b.OutputCode = cr.OutputCode
                                          WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current'' AND ru.PeriodKey = ti.PeriodKey
                                            AND (ru.DocumentId = d.Id
                                                 OR (ru.DocumentId IS NULL AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS ru2
                                                      WHERE ru2.ProjectId = d.ProjectId AND ru2.Status = N''Current''
                                                        AND ru2.DocumentId = d.Id AND ru2.PeriodKey = ti.PeriodKey)))
                                            AND (cr.TableInstanceId = ti.Id
 OR cr.TableInstanceId IS NULL)) AS u
                                    GROUP BY u.SourceRowKey, u.BindingId, u.ColumnDefId, u.Own, u.SubstanceEntryId) AS g
                              GROUP BY g.SourceRowKey, g.BindingId, g.ColumnDefId) AS t) AS q
                  GROUP BY q.SourceRowKey) AS rvx ON rvx.SourceRowKey = r0.RowKey
             WHERE r0.PeriodKey = ti.PeriodKey AND r0.TableInstanceId = ti.Id AND r0.IsDeleted = 0) AS r';
            SET @select = @selectRv;
            SET @extra = @extra + 7;
        END
        ELSE IF @rvIds <> N'' AND @rvPred = 1 AND @n + @extra + 8 <= 250
        BEGIN
            SET @rvApply = N'
OUTER APPLY (SELECT ' + STUFF(@rvAgg, 1, 2, N'') + N'
             FROM (SELECT t.ColumnDefId, RIGHT(N''0000000000'' + CAST(t.BindingId AS nvarchar(10)), 10) + COALESCE(CONVERT(nvarchar(60), t.Total), N''N'') AS Pack
                   FROM (SELECT g.BindingId, g.ColumnDefId,
                                CASE WHEN MAX(g.Own) = 1
                                     THEN CASE WHEN MAX(CASE WHEN g.Own = 1 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 1 THEN g.SubTotal END) END
                                     ELSE CASE WHEN MAX(CASE WHEN g.Own = 0 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 0 THEN g.SubTotal END) END
                                END AS Total
                         FROM (SELECT u.BindingId, u.ColumnDefId, u.Own, MIN(u.Value) AS SubTotal,
                                      CASE WHEN MIN(u.Value) <> MAX(u.Value) OR MIN(u.UnitId) <> MAX(u.UnitId) THEN 1 ELSE 0 END AS Amb
                               FROM (SELECT b.Id AS BindingId, b.ColumnDefId, cr.SubstanceEntryId, cr.Value, cr.UnitId,
                                            CASE WHEN cr.TableInstanceId IS NULL THEN 0 ELSE 1 END AS Own
                                     FROM cfg.CalculationBinding AS b
                                     JOIN calc.MethodologyVersion AS mv ON mv.MethodologyId = b.MethodologyId
                                     JOIN calc.CalculationResult AS cr ON cr.PeriodKey = ti.PeriodKey AND cr.DocumentId = d.Id
                                          AND cr.MethodologyVersionId = mv.Id AND cr.OutputCode = b.OutputCode AND cr.SourceRowKey = r.RowKey
                                     JOIN (SELECT ru.Id FROM calc.CalculationRun AS ru
                                            WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current'' AND ru.PeriodKey = ti.PeriodKey
                                              AND (ru.DocumentId = d.Id
                                                   OR (ru.DocumentId IS NULL AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS ru2
                                                        WHERE ru2.ProjectId = d.ProjectId AND ru2.Status = N''Current''
                                                          AND ru2.DocumentId = d.Id AND ru2.PeriodKey = ti.PeriodKey)))) AS rr
                                          ON rr.Id = cr.CalculationRunId
                                     WHERE b.IsActive = 1 AND b.TableDefId = ' + CAST(@id AS nvarchar(10)) + N' AND b.ColumnDefId IN (' + STUFF(@rvIds, 1, 2, N'') + N')
                                       AND (cr.TableInstanceId = ti.Id OR cr.TableInstanceId IS NULL)';
            SET @rvApply = @rvApply + N'
                                       AND (REPLACE(b.MatchJson, CHAR(32), N'''') = N''{}''
                                            OR (ISJSON(b.MatchJson) = 1 AND LEFT(LTRIM(b.MatchJson), 1) = N''{''
                                                AND NOT EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(b.MatchJson) = 1 THEN b.MatchJson ELSE N''{}'' END) AS j
                                                     WHERE NOT EXISTS (SELECT 1 FROM doc.CellValue AS m
                                                          WHERE m.PeriodKey = r.PeriodKey AND m.TableRowId = r.Id
                                                            AND CAST(m.ColumnDefId AS nvarchar(20)) = j.[key] COLLATE Latin1_General_BIN2
                                                            AND COALESCE(m.ValueString, CONVERT(nvarchar(64), m.ValueNumeric), CONVERT(nvarchar(20), m.ValueRegistryEntryId),
                                                                         CASE m.ValueBool WHEN 1 THEN N''True'' WHEN 0 THEN N''False'' END) COLLATE Latin1_General_BIN2
                                                              = CASE WHEN j.[type] = 3 THEN CASE j.[value] WHEN N''true'' THEN N''True'' ELSE N''False'' END ELSE j.[value] END COLLATE Latin1_General_BIN2))))) AS u
                               GROUP BY u.BindingId, u.ColumnDefId, u.Own, u.SubstanceEntryId) AS g
                         GROUP BY g.BindingId, g.ColumnDefId) AS t) AS q) AS rv';
            SET @joins = @joins + @rvApply;
            SET @select = @selectRv;
            SET @extra = @extra + 8;
        END

        -- ⚠ Колонки — у порядку `Id`, а не `Ordinal`: `Ordinal` — лише
        -- відображення (D-17), і перестановка колонок у новій версії не має
        -- міняти порядок колонок уже опублікованої вʼюхи. Ліміт SQL Server —
        -- 256 таблиць у запиті: 5 службових + по одній на колонку.
        -- Q-17.7: у MonthsInColumns кожна введена колонка додає ще одну таблицю (комірка власника), плюс
        -- екземпляр+рядок на потрібний місяць і якір (@extra) — ліміт той самий, лічильник більший.
        IF @n + @extra > 250
        BEGIN
            -- ⚠ Не THROW тут: на старті (виклик без параметра) одна задовга
            -- таблиця не має лишати без вʼюх усі інші. Помилка — наприкінці.
            -- ⚠ У повідомленні — заголовок (шаблон, версія, аркуш, таблиця): його
            -- читає /health/ready і журнал публікації, там голий Id нічого не каже.
            SET @failure = COALESCE(@failure, CONCAT(N'rpt.usp_GenerateTemplateViews: ', @header, N' (таблиця ', @id,
                                  N') має ', @n, N' колонок',
                                  CASE WHEN @extra <> 0 THEN CONCAT(N' (з приєднаннями власників значень — ', @n + @extra, N' таблиць у запиті)') ELSE N'' END,
                                  N', вʼюха вміщує щонайбільше 250.'));
            GOTO next_table;
        END

        -- Q-17.perf: ведучий набір - екземпляри (ti, d, p); власники-місяці (oim) і результати методологій (rv) рахуються ОДИН раз
        -- на екземпляр, а рядки таблиці йдуть слідом (JOIN або CROSS APPLY з колонками rv<n>). Без цього (прапор вимкнено,
        -- Calculated немає) текст FROM лишається таким, як був.
        -- Q-17.perf2 (Аудит-2: seek calc.CalculationResult торкається 3-12 із 25 партицій навіть з предикатом _PeriodKey): коли рядки йдуть
        -- CROSS APPLY (r - похідна apply на екземпляр), предикат _PeriodKey на r.PeriodKey не потрапляє в екземпляр ti (apply рахується для
        -- КОЖНОГО екземпляра документа), тож seek по партиціях виконується для всіх періодів. _PeriodKey береться з ti.PeriodKey (те саме
        -- значення: r.PeriodKey = ti.PeriodKey) - тоді предикат відсікає екземпляри ДО apply, а cr.PeriodKey = ti.PeriodKey - відсічка партицій.
        SET @pkExpr = CASE WHEN @rowApply = N'' THEN N'r.PeriodKey' ELSE N'ti.PeriodKey' END;
        SET @fromSql = CASE WHEN @rowApply = N'' AND @tiAp = N'' THEN N'FROM doc.TableInstance AS ti
JOIN doc.TableRow      AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.Document      AS d ON d.Id = ti.DocumentId
JOIN doc.Project       AS p ON p.Id = d.ProjectId'
            ELSE N'FROM doc.TableInstance AS ti
JOIN doc.Document      AS d ON d.Id = ti.DocumentId
JOIN doc.Project       AS p ON p.Id = d.ProjectId' + @tiAp + CASE WHEN @rowApply <> N'' THEN @rowApply ELSE N'
JOIN doc.TableRow      AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0' END END;

        -- ⛔ RS (копії аркушів, людина 03.11): стан подання — СВОЄЇ копії (SheetCopyId екземпляра), номер у групі і назва
        -- «<Назва> N» (мова en, інакше код аркуша; з номером лише коли в групі є копії). Назва аркуша версії — літерал
        -- (опублікована версія незмінна); група шукається за аркушем ЦІЄЇ версії.
        SET @sheetNameLit = N'N''' + REPLACE(COALESCE((SELECT COALESCE(JSON_VALUE(sdn.NameL10n, N'$.en'), sdn.Code)
                                                         FROM cfg.SheetDef AS sdn WHERE sdn.Id = @sheet), N''), N'''', N'''''') + N'''';
        SET @copyCols = CONCAT(N',
       CAST(COALESCE(dsh.CopyOrdinal, 1) AS smallint) AS _SheetCopyOrdinal,
       (SELECT CONCAT(COALESCE(JSON_VALUE(MAX(CASE WHEN g.CopyOfId IS NULL THEN g.NameOverrideL10n END), N''$.en''), ', @sheetNameLit, N'),
                      CASE WHEN ti.SheetCopyId IS NOT NULL OR MAX(CASE WHEN g.CopyOfId IS NOT NULL THEN 1 ELSE 0 END) = 1
                           THEN CONCAT(N'' '', COALESCE(dsh.CopyOrdinal, 1)) ELSE N'''' END)
        FROM doc.DocumentSheet AS g WHERE g.DocumentId = d.Id AND g.SheetDefId = ', CAST(@sheet AS nvarchar(10)), N') AS _SheetDisplayName');

        SET @sql = CONCAT(
N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
AS
', @marker, N': ', @header, N'. Не редагувати руками.
SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
       d.Id AS _DocumentId, d.BusinessKey AS _DocumentKey,
       ', @pkExpr, N' AS _PeriodKey,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS _Status,
       r.Id AS _RowId, r.RowKey AS _RowKey, r.Ordinal AS _RowOrdinal', @select, @copyCols, N'
', @fromSql, N'
LEFT JOIN wf.ApprovalState AS a ON a.DocumentId = d.Id AND a.SheetDefId = ', CAST(@sheet AS nvarchar(10)),
N' AND a.PeriodKey = ti.PeriodKey
                               AND (a.SheetCopyId = ti.SheetCopyId OR (a.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
LEFT JOIN doc.DocumentSheet AS dsh ON dsh.Id = ti.SheetCopyId', @joins, N'
WHERE ti.TableDefId = ', CAST(@id AS nvarchar(10)), N';');

        -- ⚠ Порівнюється текст від позначки, а не весь: заголовок
        -- `CREATE OR ALTER VIEW` сервер не зобов'язаний зберігати дослівно.
        -- Вʼюха з тим самим ім'ям, зроблена руками (позначки немає), перезаписується.
        SET @existing = OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V'));
        SET @pos = CHARINDEX(@marker, @existing);
        SET @ddl = CASE WHEN @existing IS NULL OR @pos = 0
                             OR SUBSTRING(@existing, @pos, LEN(@existing)) COLLATE Latin1_General_BIN2
                                <> SUBSTRING(@sql, CHARINDEX(@marker, @sql), LEN(@sql)) COLLATE Latin1_General_BIN2
                        THEN 1 ELSE 0 END;
        -- Q-17.slotmap: рядки rpt.ViewSlot цієї вʼюхи = бажана мапа (вʼюха без маршрутизації - жодного рядка). Різниця - переписати
        -- мапу й (за потреби) вʼюху в ОДНІЙ транзакції: читач не бачить вʼюху з чужою мапою (ALTER VIEW тримає Sch-M до коміту).
        SET @mapDiff = CASE WHEN EXISTS (SELECT ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1
                                         EXCEPT SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = @view)
                              OR EXISTS (SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = @view
                                         EXCEPT SELECT ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1)
                            THEN 1 ELSE 0 END;

        IF @ddl = 1 OR @mapDiff = 1
        BEGIN
            BEGIN TRANSACTION;
            IF @mapDiff = 1
            BEGIN
                DELETE FROM rpt.ViewSlot WHERE ViewName = @view;
                INSERT INTO rpt.ViewSlot (ViewName, ColumnDefId, Slot, Mo) SELECT @view, ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1;
            END
            IF @ddl = 1
                EXEC sys.sp_executesql @sql;
            COMMIT TRANSACTION;
        END

        IF @existing IS NULL
            INSERT INTO @result VALUES (@id, @view, N'Created');
        ELSE IF @ddl = 1 OR @mapDiff = 1
            INSERT INTO @result VALUES (@id, @view, N'Altered');
        ELSE
            INSERT INTO @result VALUES (@id, @view, N'Unchanged');

next_table:
        FETCH NEXT FROM cur_views INTO @id, @sheet, @header, @view;
    END
    CLOSE cur_views;
    DEALLOCATE cur_views;

    IF @clash IS NOT NULL
    BEGIN
        SET @message = CONCAT(
            N'rpt.usp_GenerateTemplateViews: дві таблиці дають одне ім''я вʼюхи rpt.', @clash,
            N' (', @clashWho, N'). Змініть код шаблону, аркуша чи таблиці так, щоб вони відрізнялися не лише розділовими знаками чи регістром.');
        THROW 50409, @message, 1;
    END

    -- Q-17.slotmap: рядки мапи вʼюх, яких у базі вже немає, - прибрати (лише при повній генерації; рядки іншої процедури
    -- належать наявним вʼюхам і лишаються).
    IF @TemplateVersionId IS NULL
        DELETE FROM rpt.ViewSlot WHERE OBJECT_ID(N'rpt.' + QUOTENAME(ViewName), N'V') IS NULL;

    IF @failure IS NOT NULL
        THROW 50422, @failure, 1;

    -- AN-155d: відбиток пишемо лише після успішної ПОВНОЇ генерації; збій запису не ламає генерацію (наступний старт перегенерує).
    IF @TemplateVersionId IS NULL
    BEGIN TRY
        IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 3 AND major_id = SCHEMA_ID(N'rpt') AND minor_id = 0 AND name = @fpName)
            EXEC sys.sp_updateextendedproperty @name = @fpName, @value = @fp, @level0type = N'SCHEMA', @level0name = N'rpt';
        ELSE
            EXEC sys.sp_addextendedproperty @name = @fpName, @value = @fp, @level0type = N'SCHEMA', @level0name = N'rpt';
    END TRY
    BEGIN CATCH
        -- нічого: без відбитка наступний старт просто згенерує все.
        SET @fp = @fp;
    END CATCH

    SELECT TableDefId, N'rpt.' + ViewName AS ViewName, Action FROM @result ORDER BY TableDefId;
END;
GO

-- ══ RPT-STABLE (Part A): version-less report views ═══════════════════════════
-- One view per (Template, Sheet, Table) across ALL Published/Deprecated versions: rpt.v_<Tpl>_<Sheet>_<Table> (no version
-- suffix, same sanitising / 128-char hash rule as the _vN views). The _vN views and rpt.usp_GenerateTemplateViews are NOT
-- touched (D-53 contract kept, golden guard stays byte-identical); this is a separate procedure called by the application
-- right after it (ReportViewGenerator), or by a DBA:  EXEC rpt.usp_GenerateStableViews [@TemplateVersionId], [@MonthRouting = 1].
--   * rows       : WHERE ti.TableDefId IN (that table in every published version);
--   * columns    : UNION by ColumnDef.Code over those versions, ordered by first appearance (version creation order, Id),
--                  NEVER removed (a column dropped in v3 stays, NULL for rows of v3);
--   * read       : ONE pass - a single OUTER APPLY pc over doc.CellValue with MAX(CASE WHEN ColumnDefId IN (<ids of the code>) ..)
--                  instead of a LEFT JOIN per column; month-routed (Q-17) columns use the same sv/sc/fam owner scheme as _vN;
--   * _TemplateVersion : service column, nvarchar, taken PER ROW from the version of the row's own TableDef (a constant CASE
--                  over ti.TableDefId, no extra join), never from Project.TemplateVersionId - one project may show some periods
--                  as v1 and later periods as v2;
--   * type conflict : a code whose storage family differs between versions (e.g. string in v1, number in v2) becomes nvarchar
--                  (every value converted to text); the proc returns a Warning for it; such a column is not month-routed and
--                  not overlaid with methodology results;
--   * limits     : more than 1000 columns (SQL Server view limit 1024) -> 50422 for that table, the others are generated;
--   * names      : a stable name equal to another stable name or to any _vN name is NOT generated (50409 at the end).
-- Idempotent: the text is a pure function of the structure; an equal existing view is left alone (Unchanged).
-- Result set: ViewName, Action (Created / Altered / Unchanged), Warning (nullable).
-- AN-155d: @OnlyIfChanged = 1 — як у rpt.usp_GenerateTemplateViews (власна розширена властивість `ecr.stableviews.fingerprint`);
-- пропуск повертає один рядок (N'', N'Skipped', NULL).
CREATE OR ALTER PROCEDURE rpt.usp_GenerateStableViews
    @TemplateVersionId int = NULL,
    @MonthRouting bit = 0,
    @OnlyIfChanged bit = 0
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @fp varchar(64) = rpt.fn_ViewsFingerprint(@MonthRouting);
    DECLARE @fpName sysname = N'ecr.stableviews.fingerprint';

    DECLARE @result TABLE (ViewName sysname NOT NULL, Action nvarchar(16) NOT NULL, Warning nvarchar(2000) NULL);

    -- Every table of every published version.
    DECLARE @gt TABLE (
        TableDefId int NOT NULL PRIMARY KEY, SheetDefId int NOT NULL, TemplateVersionId int NOT NULL, TemplateId int NOT NULL,
        TemplateCode nvarchar(64) NOT NULL, SheetCode nvarchar(64) NOT NULL, TableCode nvarchar(64) NOT NULL,
        VersionText nvarchar(20) NOT NULL, LayoutKind tinyint NOT NULL, GroupId int NULL, Routed bit NOT NULL);
    -- D-474: Routed - маршрутизація місяців ВЕРСІЇ таблиці: прапор (усі версії) або розкладка версії YearWithMonthColumns (1).
    INSERT INTO @gt (TableDefId, SheetDefId, TemplateVersionId, TemplateId, TemplateCode, SheetCode, TableCode, VersionText, LayoutKind, Routed)
    SELECT td.Id, sd.Id, tv.Id, t.Id, t.Code, sd.Code, td.Code, tv.[Version], td.LayoutKind,
           CASE WHEN @MonthRouting = 1 OR tv.Layout = 1 THEN 1 ELSE 0 END
      FROM cfg.TableDef AS td
      JOIN cfg.SheetDef AS sd ON sd.Id = td.SheetDefId
      JOIN cfg.TemplateVersion AS tv ON tv.Id = sd.TemplateVersionId
      JOIN cfg.Template AS t ON t.Id = tv.TemplateId
     WHERE tv.Status IN (1, 2) AND td.IsDeleted = 0 AND sd.IsDeleted = 0;

    DECLARE @groups TABLE (
        GroupId int IDENTITY(1, 1) PRIMARY KEY, TemplateId int NOT NULL, TemplateCode nvarchar(64) NOT NULL,
        SheetCode nvarchar(64) NOT NULL, TableCode nvarchar(64) NOT NULL, Header nvarchar(400) NOT NULL, ViewName nvarchar(400) NOT NULL);
    INSERT INTO @groups (TemplateId, TemplateCode, SheetCode, TableCode, Header, ViewName)
    SELECT g.TemplateId, g.TemplateCode, g.SheetCode, g.TableCode,
           CONCAT(N'template ', g.TemplateCode, N', sheet ', g.SheetCode, N', table ', g.TableCode, N', all versions'),
           CONCAT(N'v_', g.TemplateCode, N'_', g.SheetCode, N'_', g.TableCode)
      FROM (SELECT DISTINCT TemplateId, TemplateCode, SheetCode, TableCode FROM @gt) AS g;
    UPDATE x SET GroupId = g.GroupId
      FROM @gt AS x
      JOIN @groups AS g ON g.TemplateId = x.TemplateId AND g.SheetCode = x.SheetCode AND g.TableCode = x.TableCode;

    -- Names: stable ('S') and per-version ('V') names go through the same sanitising / hashing rule as the _vN views.
    DECLARE @nm TABLE (Kind char(1) NOT NULL, Ref int NOT NULL, Name nvarchar(400) NOT NULL, PRIMARY KEY (Kind, Ref));
    INSERT INTO @nm (Kind, Ref, Name)
    SELECT 'S', GroupId, ViewName FROM @groups
    UNION ALL
    SELECT 'V', TableDefId, CONCAT(N'v_', TemplateCode, N'_', SheetCode, N'_', TableCode, N'_v', VersionText) FROM @gt;

    DECLARE @k char(1), @ref int, @raw nvarchar(400), @pos int;
    DECLARE cur_names CURSOR LOCAL FAST_FORWARD FOR SELECT Kind, Ref FROM @nm;
    OPEN cur_names;
    FETCH NEXT FROM cur_names INTO @k, @ref;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @raw = Name FROM @nm WHERE Kind = @k AND Ref = @ref;
        SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        WHILE @pos > 0
        BEGIN
            SET @raw = STUFF(@raw, @pos, 1, N'_');
            SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        END
        IF LEN(@raw) > 128
            SET @raw = LEFT(@raw, 115) + N'_' + LEFT(CONVERT(varchar(64), HASHBYTES('SHA2_256', @raw), 2), 12);
        UPDATE @nm SET Name = @raw WHERE Kind = @k AND Ref = @ref;
        FETCH NEXT FROM cur_names INTO @k, @ref;
    END
    CLOSE cur_names;
    DEALLOCATE cur_names;

    UPDATE g SET ViewName = n.Name FROM @groups AS g JOIN @nm AS n ON n.Kind = 'S' AND n.Ref = g.GroupId;

    -- A stable name that equals another stable name or any _vN name is not generated (collation of the database, as the server compares names).
    -- As in the _vN proc, only the template of the requested version matters: a clash elsewhere does not block this call.
    DELETE FROM @groups
     WHERE @TemplateVersionId IS NOT NULL
       AND TemplateId <> (SELECT TemplateId FROM cfg.TemplateVersion WHERE Id = @TemplateVersionId);
    DECLARE @clashNames TABLE (Name nvarchar(400) NOT NULL);
    INSERT INTO @clashNames
    SELECT n.Name FROM @nm AS n GROUP BY n.Name
     HAVING COUNT(*) > 1 AND MAX(CASE n.Kind WHEN 'S' THEN 1 ELSE 0 END) = 1
        AND n.Name IN (SELECT g.ViewName FROM @groups AS g);
    DECLARE @clash nvarchar(400) = (SELECT TOP (1) Name FROM @clashNames ORDER BY Name);
    DECLARE @clashWho nvarchar(1500) = STUFF((
        SELECT N'; ' + g.Header FROM @groups AS g WHERE g.ViewName = @clash ORDER BY g.GroupId
        FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
    DELETE FROM @groups WHERE ViewName IN (SELECT Name FROM @clashNames);

    IF @OnlyIfChanged = 1 AND @TemplateVersionId IS NULL
       AND @fp = (SELECT CONVERT(varchar(64), ep.value) FROM sys.extended_properties AS ep
                   WHERE ep.class = 3 AND ep.major_id = SCHEMA_ID(N'rpt') AND ep.minor_id = 0 AND ep.name = @fpName)
       AND NOT EXISTS (SELECT 1 FROM @groups AS gg WHERE OBJECT_ID(N'rpt.' + QUOTENAME(gg.ViewName), N'V') IS NULL)
       -- Q-17.slotmap: a view that reads the slot map has its rpt.ViewSlot rows (otherwise regenerate, do not skip).
       AND NOT EXISTS (SELECT 1 FROM @groups AS gg
                         JOIN sys.sql_expression_dependencies AS dep
                           ON dep.referencing_id = OBJECT_ID(N'rpt.' + QUOTENAME(gg.ViewName), N'V') AND dep.referenced_id = OBJECT_ID(N'rpt.ViewSlot', N'U')
                        WHERE NOT EXISTS (SELECT 1 FROM rpt.ViewSlot AS vs WHERE vs.ViewName = gg.ViewName))
    BEGIN
        SELECT CAST(N'' AS sysname) AS ViewName, CAST(N'Skipped' AS nvarchar(16)) AS Action, CAST(NULL AS nvarchar(2000)) AS Warning;
        RETURN;
    END

    DECLARE @g int, @header nvarchar(400), @view sysname, @tids nvarchar(max), @sheetCase nvarchar(max), @verCase nvarchar(max);
    DECLARE @select nvarchar(max), @selectText nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
    DECLARE @marker nvarchar(64) = N'-- Згенеровано rpt.usp_GenerateStableViews';
    DECLARE @failure nvarchar(2048), @warning nvarchar(2000);
    DECLARE @seq int, @colCode nvarchar(64), @colType tinyint, @isMonth bit, @monthNo tinyint, @conflict bit, @ids nvarchar(max), @pred nvarchar(max);
    DECLARE @n int, @plainCnt int, @plainAgg nvarchar(max), @valueCol nvarchar(max), @valueColRv nvarchar(max), @numRef nvarchar(64), @strRef nvarchar(64), @raw1 nvarchar(64);
    DECLARE @routedGroup bit, @routed bit, @layoutOk bit, @monthMask int, @needAnchor bit, @ownerRows nvarchar(max), @ownerCells nvarchar(max);
    DECLARE @agg nvarchar(max), @aggBody nvarchar(max), @famAgg nvarchar(max), @pkExpr nvarchar(16), @fromSql nvarchar(max), @rowApply nvarchar(max);
    DECLARE @selectRv nvarchar(max), @selectTextRv nvarchar(max), @rvIds nvarchar(max), @rvApply nvarchar(max), @rvAgg nvarchar(max), @rvPack nvarchar(max), @rvExprs nvarchar(max);
    DECLARE @rvPred bit, @rvRef nvarchar(8), @conflictCodes nvarchar(1500), @hasText bit, @hasId bit, @extra int;
    -- RS (копії аркушів): службові колонки _SheetCopyOrdinal/_SheetDisplayName — у КІНЦІ списку (адитивні).
    DECLARE @sheetNameCase nvarchar(max), @copyCols nvarchar(max);
    -- Q-17.slotmap: як у rpt.usp_GenerateTemplateViews - агрегати за слотом і бажані рядки rpt.ViewSlot (усі Id коду й аліасів).
    DECLARE @plainAggS nvarchar(max), @scAggS nvarchar(max), @vnLit nvarchar(300), @useSlots bit, @mapDiff bit, @ddl bit, @slotAgg nvarchar(max);
    DECLARE @slot TABLE (ColumnDefId int NOT NULL, Slot int NOT NULL, Mo int NULL, PRIMARY KEY (ColumnDefId, Slot));
    -- D-474: група з версіями різної маршрутизації (прапор OFF, Layout = 1 лише в частини версій) - @mixedGuard обмежує
    -- значення власника (fam) рядками маршрутизованих версій (@routedTids); без змішування - порожній (текст як до D-474).
    DECLARE @routedTids nvarchar(max), @mixedGuard nvarchar(max);

    -- Columns of one group: one row per ColumnDef (all versions), then one row per code.
    -- D-474: Routed - версія колонки маршрутизується (Mo у мапі слотів - лише для Id маршрутизованих версій).
    DECLARE @ci TABLE (ColId int NOT NULL PRIMARY KEY, Code nvarchar(64) NOT NULL, DataType tinyint NOT NULL, Fam tinyint NOT NULL,
                       IsMonth bit NOT NULL, MonthNo tinyint NULL, Ord bigint NOT NULL, Routed bit NOT NULL);
    DECLARE @cols TABLE (Seq int IDENTITY(1, 1) PRIMARY KEY, Code nvarchar(64) NOT NULL, FirstOrd bigint NOT NULL);
    -- RPT-STABLE Part B: code aliases of one group from the persisted rename map (cfg.ColumnRename): a renamed column is
    -- exposed under BOTH names, each reading the data of every id of the whole alias class (transitive closure).
    DECLARE @alias TABLE (Code nvarchar(64) NOT NULL, Other nvarchar(64) NOT NULL, PRIMARY KEY (Code, Other));

    DECLARE cur_views CURSOR LOCAL FAST_FORWARD FOR
        SELECT GroupId, Header, ViewName FROM @groups ORDER BY GroupId;
    OPEN cur_views;
    FETCH NEXT FROM cur_views INTO @g, @header, @view;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @select = N'', @selectText = N'', @joins = N'', @n = 0, @plainCnt = 0, @plainAgg = N'', @ownerRows = N'', @ownerCells = N'',
               @monthMask = 0, @needAnchor = 0, @famAgg = N'', @selectRv = N'', @selectTextRv = N'', @rvIds = N'', @rvApply = N'',
               @rvAgg = N'', @rvPack = N'', @rvExprs = N'', @rowApply = N'', @conflictCodes = N'', @extra = 0,
               @plainAggS = N'', @scAggS = N'', @vnLit = N'N''' + REPLACE(@view, N'''', N'''''') + N'''';
        DELETE FROM @slot;
        DELETE FROM @ci;
        DELETE FROM @cols;

        SET @tids = STUFF((SELECT N',' + CAST(TableDefId AS nvarchar(10)) FROM @gt WHERE GroupId = @g ORDER BY TableDefId
                           FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'');
        SET @sheetCase = N'CASE ti.TableDefId' + (SELECT N' WHEN ' + CAST(TableDefId AS nvarchar(10)) + N' THEN ' + CAST(SheetDefId AS nvarchar(10))
                           FROM @gt WHERE GroupId = @g ORDER BY TableDefId FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)') + N' END';
        SET @verCase = N'CAST(CASE ti.TableDefId' + (SELECT N' WHEN ' + CAST(TableDefId AS nvarchar(10)) + N' THEN N' + QUOTENAME(VersionText, '''')
                           FROM @gt WHERE GroupId = @g ORDER BY TableDefId FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)') + N' END AS nvarchar(20))';

        -- Month routing needs the MonthsInColumns layout (0) in EVERY version of the table.
        -- D-474: and at least one routed version (flag ON = all versions; flag OFF = versions with Layout = 1). Flag ON or no
        -- Layout = 1 version: exactly as before D-474.
        SET @routedGroup = CASE WHEN EXISTS (SELECT 1 FROM @gt WHERE GroupId = @g AND Routed = 1)
                                 AND NOT EXISTS (SELECT 1 FROM @gt WHERE GroupId = @g AND LayoutKind <> 0) THEN 1 ELSE 0 END;
        -- D-474: mixed group (some versions not routed) - owner values (fam) only for the rows of the routed versions; a row of a
        -- non-routed version keeps its own copy even when the same document has a routed-version instance in that year.
        SET @routedTids = STUFF((SELECT N',' + CAST(TableDefId AS nvarchar(10)) FROM @gt WHERE GroupId = @g AND Routed = 1 ORDER BY TableDefId
                                 FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'');
        SET @mixedGuard = CASE WHEN @routedGroup = 1 AND EXISTS (SELECT 1 FROM @gt WHERE GroupId = @g AND Routed = 0)
                               THEN N' AND ti.TableDefId IN (' + @routedTids + N')' ELSE N'' END;
        SET @rvPred = CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding AS cb JOIN @gt AS x ON x.TableDefId = cb.TableDefId
                                         WHERE x.GroupId = @g AND cb.IsActive = 1 AND REPLACE(cb.MatchJson, CHAR(32), N'') <> N'{}') THEN 1 ELSE 0 END;
        SET @rvRef = CASE WHEN @rvPred = 0 THEN N'r' ELSE N'rv' END;

        INSERT INTO @ci (ColId, Code, DataType, Fam, IsMonth, MonthNo, Ord, Routed)
        SELECT cd.Id, cd.Code, cd.DataType,
               CASE cd.DataType WHEN 0 THEN 0 WHEN 3 THEN 3 WHEN 4 THEN 4 WHEN 5 THEN 5 WHEN 7 THEN 7 ELSE 1 END,
               cd.IsMonthColumn, cd.MonthNumber,
               CAST(x.TemplateVersionId AS bigint) * 4294967296 + cd.Id, x.Routed
          FROM cfg.ColumnDef AS cd JOIN @gt AS x ON x.TableDefId = cd.TableDefId
         WHERE x.GroupId = @g AND cd.IsDeleted = 0;
        INSERT INTO @cols (Code, FirstOrd) SELECT Code, MIN(Ord) FROM @ci GROUP BY Code ORDER BY MIN(Ord);

        DELETE FROM @alias;
        INSERT INTO @alias (Code, Other)
        SELECT DISTINCT a.Code, b.Code FROM cfg.ColumnRename AS r
          JOIN @ci AS a ON a.ColId = r.FromColumnDefId JOIN @ci AS b ON b.ColId = r.ToColumnDefId WHERE a.Code <> b.Code
        UNION
        SELECT DISTINCT b.Code, a.Code FROM cfg.ColumnRename AS r
          JOIN @ci AS a ON a.ColId = r.FromColumnDefId JOIN @ci AS b ON b.ColId = r.ToColumnDefId WHERE a.Code <> b.Code;
        WHILE @@ROWCOUNT > 0
            INSERT INTO @alias (Code, Other)
            SELECT DISTINCT x.Code, y.Other FROM @alias AS x JOIN @alias AS y ON y.Code = x.Other
             WHERE x.Code <> y.Other AND NOT EXISTS (SELECT 1 FROM @alias AS z WHERE z.Code = x.Code AND z.Other = y.Other);

        SET @seq = (SELECT MIN(Seq) FROM @cols);
        WHILE @seq IS NOT NULL
        BEGIN
            SET @colCode = (SELECT Code FROM @cols WHERE Seq = @seq);
            SET @n = @n + 1;
            -- Type / month placement come from the LATEST version that has the code.
            SELECT TOP (1) @colType = DataType, @isMonth = IsMonth, @monthNo = MonthNo
              FROM @ci WHERE Code = @colCode ORDER BY Ord DESC;
            SET @conflict = CASE WHEN (SELECT COUNT(DISTINCT Fam) FROM @ci WHERE Code = @colCode) > 1 THEN 1 ELSE 0 END;
            SET @ids = STUFF((SELECT N',' + CAST(ColId AS nvarchar(10)) FROM @ci
                               WHERE Code = @colCode OR Code IN (SELECT Other FROM @alias WHERE Code = @colCode) ORDER BY ColId
                              FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'');
            SET @pred = CASE WHEN CHARINDEX(N',', @ids) > 0 THEN N'x.ColumnDefId IN (' + @ids + N')' ELSE N'x.ColumnDefId = ' + @ids END;
            SET @raw1 = CASE @colType WHEN 0 THEN N'ValueString' WHEN 3 THEN N'ValueBool' WHEN 4 THEN N'ValueDate'
                                      WHEN 5 THEN N'ValueRegistryEntryId' WHEN 7 THEN N'ValueUnitId' ELSE N'ValueNumeric' END;
            SET @numRef = N'pc.v' + CAST(@n AS nvarchar(10));
            SET @strRef = N'pc.s' + CAST(@n AS nvarchar(10));
            SET @routed = CASE WHEN @routedGroup = 1 AND @conflict = 0 AND @colType NOT IN (6, 8)
                                    AND (@isMonth = 0 OR @monthNo BETWEEN 1 AND 12) THEN 1 ELSE 0 END;
            -- Q-17.slotmap: кожен Id коду (з аліасами) - рядок мапи у слоті @n; Mo - лише для маршрутизованої колонки
            -- (D-474: і лише для Id маршрутизованої версії - Id немаршрутизованої читаються як власна копія, pc).
            INSERT INTO @slot (ColumnDefId, Slot, Mo)
            SELECT ColId, @n, CASE WHEN @routed = 1 AND Routed = 1 THEN CASE WHEN @isMonth = 1 THEN @monthNo ELSE 0 END END
              FROM @ci WHERE Code = @colCode OR Code IN (SELECT Other FROM @alias WHERE Code = @colCode);
            SET @slotAgg = CASE WHEN @colType = 3
                                THEN N'MAX(CAST(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END AS tinyint))'
                                ELSE N'MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.' + @raw1 + N' END)' END;

            IF @conflict = 1
            BEGIN
                -- Type conflict across versions: every value as text, one aggregate per storage family.
                SET @conflictCodes = @conflictCodes + CASE WHEN @conflictCodes = N'' THEN N'' ELSE N', ' END + @colCode;
                SET @agg = STUFF((SELECT N', MAX(CASE WHEN x.ColumnDefId IN (' + f.Ids + N') THEN '
                                    + CASE f.Fam WHEN 0 THEN N'x.ValueString'
                                                 WHEN 3 THEN N'CASE WHEN x.ValueBool = 1 THEN N''1'' WHEN x.ValueBool = 0 THEN N''0'' END'
                                                 WHEN 4 THEN N'CONVERT(nvarchar(30), x.ValueDate, 126)'
                                                 WHEN 5 THEN N'CONVERT(nvarchar(20), x.ValueRegistryEntryId)'
                                                 WHEN 7 THEN N'CONVERT(nvarchar(20), x.ValueUnitId)'
                                                 ELSE N'CONVERT(nvarchar(64), x.ValueNumeric)' END + N' END)'
                                    FROM (SELECT c1.Fam,
                                                 STUFF((SELECT N',' + CAST(c2.ColId AS nvarchar(10)) FROM @ci AS c2
                                                         WHERE c2.Code = c1.Code AND c2.Fam = c1.Fam ORDER BY c2.ColId
                                                           FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 1, N'') AS Ids
                                            FROM @ci AS c1 WHERE c1.Code = @colCode GROUP BY c1.Code, c1.Fam) AS f
                                   ORDER BY f.Fam FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
                SET @plainAgg = @plainAgg + N', COALESCE(' + @agg + N') AS v' + CAST(@n AS nvarchar(10));
                -- Q-17.slotmap: конфлікт типів - ті самі IN-списки (комірки його Id у потоці pc є: вони в мапі слота @n).
                SET @plainAggS = @plainAggS + N', COALESCE(' + @agg + N') AS v' + CAST(@n AS nvarchar(10));
                SET @plainCnt = @plainCnt + 1;
                SET @valueCol = @numRef;
                SET @colType = 0;   -- no companion, no methodology overlay
            END
            ELSE IF @routed = 0
            BEGIN
                -- One shared apply pc reads every non-routed column of the row by the cell key (PeriodKey, TableRowId); the cell key
                -- is unique, so MAX(CASE ..) is the value itself. bit is read as tinyint and returned as CAST(.. AS bit).
                SET @plainAgg = @plainAgg + N', '
                    + CASE WHEN @colType = 3
                           THEN N'MAX(CAST(CASE WHEN ' + @pred + N' THEN x.' + @raw1 + N' END AS tinyint))'
                           ELSE N'MAX(CASE WHEN ' + @pred + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));
                IF @colType IN (6, 8)
                    SET @plainAgg = @plainAgg + N', MAX(CASE WHEN ' + @pred + N' THEN x.ValueString END) AS s' + CAST(@n AS nvarchar(10));
                SET @plainAggS = @plainAggS + N', ' + @slotAgg + N' AS v' + CAST(@n AS nvarchar(10))
                    + CASE WHEN @colType IN (6, 8)
                           THEN N', MAX(CASE WHEN m.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN x.ValueString END) AS s' + CAST(@n AS nvarchar(10))
                           ELSE N'' END;
                SET @plainCnt = @plainCnt + 1;
                SET @valueCol = CASE WHEN @colType = 3 THEN N'CAST(' + @numRef + N' AS bit)' ELSE @numRef END;
            END
            ELSE
            BEGIN
                -- Month-routed (Q-17) input column: own computed cell (sc) > owner family value (fam) > own copy (sv); same scheme as _vN.
                SET @aggBody = CASE WHEN @colType = 3
                                THEN N'MAX(CAST(CASE WHEN ' + @pred + N' THEN x.' + @raw1 + N' END AS tinyint))'
                                ELSE N'MAX(CASE WHEN ' + @pred + N' THEN x.' + @raw1 + N' END)' END;
                SET @agg = @aggBody + N' AS v' + CAST(@n AS nvarchar(10));
                IF @isMonth = 1
                    SET @monthMask = 1;
                ELSE
                    SET @needAnchor = 1;
                -- Q-17.perf3: own copy - the shared apply pc (same rows, same aggregate), not a second apply sv.
                SET @plainAgg = @plainAgg + N', ' + @agg;
                -- Q-17.slotmap: pc and sc aggregate by slot (m.Slot) over the cells joined to the view's slot map.
                SET @plainAggS = @plainAggS + N', ' + @slotAgg + N' AS v' + CAST(@n AS nvarchar(10));
                SET @scAggS = @scAggS + N', ' + @slotAgg + N' AS cv' + CAST(@n AS nvarchar(10));
                SET @plainCnt = @plainCnt + 1;
                -- Q-17.perf3: fam aggregates by slot (fm.Slot) over the OWNED cells only (map rows with Mo, rpt.ViewSlot), see the _vN proc.
                SET @famAgg = @famAgg + N', ' + CASE WHEN @colType = 3
                    THEN N'MAX(CAST(CASE WHEN fm.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN c.' + @raw1 + N' END AS tinyint))'
                    ELSE N'MAX(CASE WHEN fm.Slot = ' + CAST(@n AS nvarchar(10)) + N' THEN c.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));
                SET @valueCol = N'COALESCE(sc.cv' + CAST(@n AS nvarchar(10)) + N', fam.v' + CAST(@n AS nvarchar(10)) + N', pc.v' + CAST(@n AS nvarchar(10)) + N')';
                IF @colType = 3
                    SET @valueCol = N'CAST(' + @valueCol + N' AS bit)';
            END

            -- Calculated (8): the methodology result (as in the API slice and _vN), materialised value as the fallback.
            SET @valueColRv = @valueCol;
            IF @colType = 8
            BEGIN
                SET @valueColRv = N'COALESCE(' + @rvRef + N'.rv' + CAST(@n AS nvarchar(10)) + N', ' + @valueCol + N')';
                SET @rvIds = @rvIds + N', ' + @ids;
                SET @rvAgg = @rvAgg + N', CAST(NULLIF(SUBSTRING(MAX(CASE WHEN q.ColumnDefId IN (' + @ids
                           + N') THEN q.Pack END), 11, 60), ''N'') AS decimal(34, 16)) AS rv' + CAST(@n AS nvarchar(10));
                SET @rvPack = @rvPack + N', MAX(CASE WHEN q.ColumnDefId IN (' + @ids + N') THEN q.Pack END) AS p' + CAST(@n AS nvarchar(10));
                SET @rvExprs = @rvExprs + N', CASE WHEN SUBSTRING(rvx.p' + CAST(@n AS nvarchar(10)) + N', 5, 1) = 0x01 THEN CAST(SUBSTRING(rvx.p'
                             + CAST(@n AS nvarchar(10)) + N', 6, 20) AS decimal(34, 16)) END AS rv' + CAST(@n AS nvarchar(10));
            END

            SET @select = @select + N',
       ' + @valueCol + N' AS ' + QUOTENAME(@colCode);
            SET @hasText = CASE WHEN @colType IN (6, 8) AND NOT EXISTS (SELECT 1 FROM @cols WHERE Code = @colCode + N'_Text') THEN 1 ELSE 0 END;
            IF @hasText = 1
            BEGIN
                SET @selectText = @selectText + N',
       CASE WHEN ' + @numRef + N' IS NULL THEN ' + @strRef + N' END AS ' + QUOTENAME(@colCode + N'_Text');
                SET @selectTextRv = @selectTextRv + N',
       CASE WHEN ' + CASE WHEN @colType = 8 THEN N'COALESCE(' + @rvRef + N'.rv' + CAST(@n AS nvarchar(10)) + N', ' + @numRef + N')' ELSE @numRef END
                                + N' IS NULL THEN ' + @strRef + N' END AS ' + QUOTENAME(@colCode + N'_Text');
            END
            SET @selectRv = @selectRv + N',
       ' + @valueColRv + N' AS ' + QUOTENAME(@colCode);
            -- PERMUSE: a Lookup column also exposes its record id under the explicit name <Code>_Id (the id is what SSRS joins on:
            -- permit id, NAE id, substance id). <Code> itself is unchanged (the same id), so existing reports keep working and the
            -- join key keeps its name if <Code> ever becomes the readable value. Skipped when the template already has a <Code>_Id
            -- column or the column is a type-conflict nvarchar column (@colType was reset to 0 above).
            SET @hasId = CASE WHEN @colType = 5 AND NOT EXISTS (SELECT 1 FROM @cols WHERE Code = @colCode + N'_Id') THEN 1 ELSE 0 END;
            IF @hasId = 1
            BEGIN
                SET @selectText = @selectText + N',
       ' + @valueCol + N' AS ' + QUOTENAME(@colCode + N'_Id');
                SET @selectTextRv = @selectTextRv + N',
       ' + @valueColRv + N' AS ' + QUOTENAME(@colCode + N'_Id');
                SET @extra = @extra + 1;
            END
            SET @seq = (SELECT MIN(Seq) FROM @cols WHERE Seq > @seq);
        END
        SET @select = @select + @selectText;
        SET @selectRv = @selectRv + @selectTextRv;

        IF @n + @extra > 1000
        BEGIN
            SET @failure = COALESCE(@failure, CONCAT(N'rpt.usp_GenerateStableViews: ', @header, N' has ', @n + @extra,
                                  N' columns, the view holds at most 1000.'));
            GOTO next_group;
        END

        -- Q-17.slotmap: a group with month-routed columns reads the slot map rpt.ViewSlot (pc, sc, fam); the others are unchanged.
        SET @useSlots = CASE WHEN @monthMask <> 0 OR @needAnchor = 1 THEN 1 ELSE 0 END;
        IF @plainCnt > 0
            SET @joins = @joins + CASE WHEN @useSlots = 1 THEN N'
OUTER APPLY (SELECT ' + STUFF(@plainAggS, 1, 2, N'') + N' FROM doc.CellValue AS x INNER LOOP JOIN rpt.ViewSlot AS m ON m.ViewName = ' + @vnLit
                + N' AND m.ColumnDefId = x.ColumnDefId WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id) AS pc' ELSE N'
OUTER APPLY (SELECT ' + STUFF(@plainAgg, 1, 2, N'') + N' FROM doc.CellValue AS x WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id) AS pc' END;

        IF @monthMask <> 0 OR @needAnchor = 1
        BEGIN
            -- Owner values (monthly: instance of the column month; entered non-monthly: year anchor) are collected ONCE per
            -- (document, year, RowKey) by one set-based pass, as in the _vN views (Q-17.perf2), over the OWNED cells only (Q-17.perf3):
            -- the owner map is the view's rpt.ViewSlot rows with Mo (Q-17.slotmap; was an inline VALUES list of every id x slot).
            SET @ownerRows = N'
LEFT HASH JOIN (SELECT c.DocumentId, c.Yr, c.RowKey' + @famAgg + N'
                  FROM rpt.ViewSlot AS fm
                  INNER HASH JOIN (SELECT oi.DocumentId, oi.PeriodKey / 100 AS Yr, oi.PeriodKey, pa.AnchorKey, ow.RowKey, x.ColumnDefId,
                                          x.ValueString, x.ValueNumeric, x.ValueDate, x.ValueBool, x.ValueRegistryEntryId, x.ValueUnitId
                                     FROM doc.TableInstance AS oi
                                     JOIN doc.Document AS od ON od.Id = oi.DocumentId
                                     LEFT JOIN (SELECT ProjectId, PeriodKey / 100 AS Yr, MIN(PeriodKey) AS AnchorKey
                                                  FROM doc.Period GROUP BY ProjectId, PeriodKey / 100) AS pa
                                            ON pa.ProjectId = od.ProjectId AND pa.Yr = oi.PeriodKey / 100
                                     JOIN doc.TableRow AS ow ON ow.PeriodKey = oi.PeriodKey AND ow.TableInstanceId = oi.Id AND ow.IsDeleted = 0
                                     JOIN doc.CellValue AS x ON x.PeriodKey = ow.PeriodKey AND x.TableRowId = ow.Id
                                    WHERE oi.TableDefId IN (' + @tids + N')) AS c
                          ON c.ColumnDefId = fm.ColumnDefId
                         AND ((fm.Mo > 0 AND c.PeriodKey % 100 = fm.Mo) OR (fm.Mo = 0 AND c.PeriodKey = c.AnchorKey))
                 WHERE fm.ViewName = ' + @vnLit + N' AND fm.Mo IS NOT NULL
                 GROUP BY c.DocumentId, c.Yr, c.RowKey) AS fam
       ON p.PeriodKind = 0 AND fam.DocumentId = ti.DocumentId AND fam.Yr = ti.PeriodKey / 100 AND fam.RowKey = r.RowKey' + @mixedGuard;
            -- Own computed cell (sc): only the computed cells of ROUTED columns (map rows with Mo) reach the aggregate.
            SET @ownerCells = N'
OUTER APPLY (SELECT ' + STUFF(@scAggS, 1, 2, N'') + N' FROM doc.CellValue AS x INNER LOOP JOIN rpt.ViewSlot AS m ON m.ViewName = ' + @vnLit
                + N' AND m.ColumnDefId = x.ColumnDefId AND m.Mo IS NOT NULL WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id AND x.IsCalculated = 1) AS sc';
            SET @joins = @ownerRows + @joins + @ownerCells;
        END

        -- Methodology results for Calculated columns: same set-based read as _vN (Q-17.recalc / perf / rvshape), over the
        -- bindings of ANY version of the table (the binding names the ColumnDef of its own version).
        IF @rvIds <> N'' AND @rvPred = 0
        BEGIN
            SET @rowApply = N'
CROSS APPLY (SELECT r0.Id, r0.RowKey, r0.Ordinal, r0.PeriodKey' + @rvExprs + N'
             FROM doc.TableRow AS r0
             LEFT HASH JOIN (SELECT q.SourceRowKey' + @rvPack + N'
                  FROM (SELECT t.SourceRowKey, t.ColumnDefId,
                               CAST(t.BindingId AS binary(4)) + CAST(CASE WHEN t.Total IS NULL THEN 0 ELSE 1 END AS binary(1))
                               + CAST(COALESCE(t.Total, 0) AS binary(20)) AS Pack
                        FROM (SELECT g.SourceRowKey, g.BindingId, g.ColumnDefId,
                                     CASE WHEN MAX(g.Own) = 1
                                          THEN CASE WHEN MAX(CASE WHEN g.Own = 1 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 1 THEN g.SubTotal END) END
                                          ELSE CASE WHEN MAX(CASE WHEN g.Own = 0 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 0 THEN g.SubTotal END) END
                                     END AS Total
                              FROM (SELECT u.SourceRowKey, u.BindingId, u.ColumnDefId, u.Own, MIN(u.Value) AS SubTotal,
                                           CASE WHEN MIN(u.Value) <> MAX(u.Value) OR MIN(u.UnitId) <> MAX(u.UnitId) THEN 1 ELSE 0 END AS Amb
                                    FROM (SELECT cr.SourceRowKey, b.Id AS BindingId, b.ColumnDefId, cr.SubstanceEntryId, cr.Value, cr.UnitId,
                                                 CASE WHEN cr.TableInstanceId IS NULL THEN 0 ELSE 1 END AS Own
                                          FROM calc.CalculationRun AS ru
                                          INNER LOOP JOIN calc.CalculationResult AS cr ON cr.PeriodKey = ti.PeriodKey AND cr.DocumentId = d.Id AND cr.CalculationRunId = ru.Id
                                          INNER HASH JOIN (SELECT bb.Id, bb.ColumnDefId, bb.OutputCode, mv.Id AS MvId
                                                             FROM cfg.CalculationBinding AS bb
                                                             JOIN calc.MethodologyVersion AS mv ON mv.MethodologyId = bb.MethodologyId
                                                            WHERE bb.IsActive = 1 AND bb.TableDefId IN (' + @tids + N') AND bb.ColumnDefId IN (' + STUFF(@rvIds, 1, 2, N'') + N')
                                                              AND REPLACE(bb.MatchJson, CHAR(32), N'''') = N''{}'') AS b
                                               ON b.MvId = cr.MethodologyVersionId AND b.OutputCode = cr.OutputCode
                                          WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current'' AND ru.PeriodKey = ti.PeriodKey
                                            AND (ru.DocumentId = d.Id
                                                 OR (ru.DocumentId IS NULL AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS ru2
                                                      WHERE ru2.ProjectId = d.ProjectId AND ru2.Status = N''Current''
                                                        AND ru2.DocumentId = d.Id AND ru2.PeriodKey = ti.PeriodKey)))
                                            AND (cr.TableInstanceId = ti.Id
 OR cr.TableInstanceId IS NULL)) AS u
                                    GROUP BY u.SourceRowKey, u.BindingId, u.ColumnDefId, u.Own, u.SubstanceEntryId) AS g
                              GROUP BY g.SourceRowKey, g.BindingId, g.ColumnDefId) AS t) AS q
                  GROUP BY q.SourceRowKey) AS rvx ON rvx.SourceRowKey = r0.RowKey
             WHERE r0.PeriodKey = ti.PeriodKey AND r0.TableInstanceId = ti.Id AND r0.IsDeleted = 0) AS r';
            SET @select = @selectRv;
        END
        ELSE IF @rvIds <> N'' AND @rvPred = 1
        BEGIN
            SET @rvApply = N'
OUTER APPLY (SELECT ' + STUFF(@rvAgg, 1, 2, N'') + N'
             FROM (SELECT t.ColumnDefId, RIGHT(N''0000000000'' + CAST(t.BindingId AS nvarchar(10)), 10) + COALESCE(CONVERT(nvarchar(60), t.Total), N''N'') AS Pack
                   FROM (SELECT g.BindingId, g.ColumnDefId,
                                CASE WHEN MAX(g.Own) = 1
                                     THEN CASE WHEN MAX(CASE WHEN g.Own = 1 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 1 THEN g.SubTotal END) END
                                     ELSE CASE WHEN MAX(CASE WHEN g.Own = 0 THEN g.Amb END) = 1 THEN NULL ELSE SUM(CASE WHEN g.Own = 0 THEN g.SubTotal END) END
                                END AS Total
                         FROM (SELECT u.BindingId, u.ColumnDefId, u.Own, MIN(u.Value) AS SubTotal,
                                      CASE WHEN MIN(u.Value) <> MAX(u.Value) OR MIN(u.UnitId) <> MAX(u.UnitId) THEN 1 ELSE 0 END AS Amb
                               FROM (SELECT b.Id AS BindingId, b.ColumnDefId, cr.SubstanceEntryId, cr.Value, cr.UnitId,
                                            CASE WHEN cr.TableInstanceId IS NULL THEN 0 ELSE 1 END AS Own
                                     FROM cfg.CalculationBinding AS b
                                     JOIN calc.MethodologyVersion AS mv ON mv.MethodologyId = b.MethodologyId
                                     JOIN calc.CalculationResult AS cr ON cr.PeriodKey = ti.PeriodKey AND cr.DocumentId = d.Id
                                          AND cr.MethodologyVersionId = mv.Id AND cr.OutputCode = b.OutputCode AND cr.SourceRowKey = r.RowKey
                                     JOIN (SELECT ru.Id FROM calc.CalculationRun AS ru
                                            WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current'' AND ru.PeriodKey = ti.PeriodKey
                                              AND (ru.DocumentId = d.Id
                                                   OR (ru.DocumentId IS NULL AND NOT EXISTS (SELECT 1 FROM calc.CalculationRun AS ru2
                                                        WHERE ru2.ProjectId = d.ProjectId AND ru2.Status = N''Current''
                                                          AND ru2.DocumentId = d.Id AND ru2.PeriodKey = ti.PeriodKey)))) AS rr
                                          ON rr.Id = cr.CalculationRunId
                                     WHERE b.IsActive = 1 AND b.TableDefId IN (' + @tids + N') AND b.ColumnDefId IN (' + STUFF(@rvIds, 1, 2, N'') + N')
                                       AND (cr.TableInstanceId = ti.Id OR cr.TableInstanceId IS NULL)';
            SET @rvApply = @rvApply + N'
                                       AND (REPLACE(b.MatchJson, CHAR(32), N'''') = N''{}''
                                            OR (ISJSON(b.MatchJson) = 1 AND LEFT(LTRIM(b.MatchJson), 1) = N''{''
                                                AND NOT EXISTS (SELECT 1 FROM OPENJSON(CASE WHEN ISJSON(b.MatchJson) = 1 THEN b.MatchJson ELSE N''{}'' END) AS j
                                                     WHERE NOT EXISTS (SELECT 1 FROM doc.CellValue AS m
                                                          WHERE m.PeriodKey = r.PeriodKey AND m.TableRowId = r.Id
                                                            AND CAST(m.ColumnDefId AS nvarchar(20)) = j.[key] COLLATE Latin1_General_BIN2
                                                            AND COALESCE(m.ValueString, CONVERT(nvarchar(64), m.ValueNumeric), CONVERT(nvarchar(20), m.ValueRegistryEntryId),
                                                                         CASE m.ValueBool WHEN 1 THEN N''True'' WHEN 0 THEN N''False'' END) COLLATE Latin1_General_BIN2
                                                              = CASE WHEN j.[type] = 3 THEN CASE j.[value] WHEN N''true'' THEN N''True'' ELSE N''False'' END ELSE j.[value] END COLLATE Latin1_General_BIN2))))) AS u
                               GROUP BY u.BindingId, u.ColumnDefId, u.Own, u.SubstanceEntryId) AS g
                         GROUP BY g.BindingId, g.ColumnDefId) AS t) AS q) AS rv';
            SET @joins = @joins + @rvApply;
            SET @select = @selectRv;
        END

        SET @pkExpr = CASE WHEN @rowApply = N'' THEN N'r.PeriodKey' ELSE N'ti.PeriodKey' END;
        SET @fromSql = CASE WHEN @rowApply = N'' THEN N'FROM doc.TableInstance AS ti
JOIN doc.TableRow      AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.Document      AS d ON d.Id = ti.DocumentId
JOIN doc.Project       AS p ON p.Id = d.ProjectId'
            ELSE N'FROM doc.TableInstance AS ti
JOIN doc.Document      AS d ON d.Id = ti.DocumentId
JOIN doc.Project       AS p ON p.Id = d.ProjectId' + @rowApply END;

        -- ⛔ RS (копії аркушів, людина 03.11): стан подання — СВОЄЇ копії; номер у групі і назва «<Назва> N» (мова en, інакше
        -- код аркуша). Назва аркуша кожної версії групи — літерал у CASE за таблицею; група шукається за аркушем версії рядка.
        SET @sheetNameCase = N'CASE ti.TableDefId' + (SELECT N' WHEN ' + CAST(x.TableDefId AS nvarchar(10)) + N' THEN N'''
                                    + REPLACE(COALESCE(JSON_VALUE(sn.NameL10n, N'$.en'), sn.Code), N'''', N'''''') + N''''
                           FROM @gt AS x JOIN cfg.SheetDef AS sn ON sn.Id = x.SheetDefId
                           WHERE x.GroupId = @g ORDER BY x.TableDefId FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)') + N' END';
        SET @copyCols = CONCAT(N',
       CAST(COALESCE(dsh.CopyOrdinal, 1) AS smallint) AS _SheetCopyOrdinal,
       (SELECT CONCAT(COALESCE(JSON_VALUE(MAX(CASE WHEN g.CopyOfId IS NULL THEN g.NameOverrideL10n END), N''$.en''), ', @sheetNameCase, N'),
                      CASE WHEN ti.SheetCopyId IS NOT NULL OR MAX(CASE WHEN g.CopyOfId IS NOT NULL THEN 1 ELSE 0 END) = 1
                           THEN CONCAT(N'' '', COALESCE(dsh.CopyOrdinal, 1)) ELSE N'''' END)
        FROM doc.DocumentSheet AS g WHERE g.DocumentId = d.Id AND g.SheetDefId = ', @sheetCase, N') AS _SheetDisplayName');

        SET @sql = CONCAT(
N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
AS
', @marker, N': ', @header, N'. Не редагувати руками.
SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
       d.Id AS _DocumentId, d.BusinessKey AS _DocumentKey,
       ', @pkExpr, N' AS _PeriodKey,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS _Status,
       r.Id AS _RowId, r.RowKey AS _RowKey, r.Ordinal AS _RowOrdinal,
       ', @verCase, N' AS _TemplateVersion', @select, @copyCols, N'
', @fromSql, N'
LEFT JOIN wf.ApprovalState AS a ON a.DocumentId = d.Id AND a.SheetDefId = ', @sheetCase,
N' AND a.PeriodKey = ti.PeriodKey
                               AND (a.SheetCopyId = ti.SheetCopyId OR (a.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
LEFT JOIN doc.DocumentSheet AS dsh ON dsh.Id = ti.SheetCopyId', @joins, N'
WHERE ti.TableDefId IN (', @tids, N');');

        SET @warning = CASE WHEN @conflictCodes = N'' THEN NULL
                            ELSE LEFT(CONCAT(N'type conflict across versions, exposed as nvarchar: ', @conflictCodes), 2000) END;
        SET @existing = OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V'));
        SET @pos = CHARINDEX(@marker, @existing);
        SET @ddl = CASE WHEN @existing IS NULL OR @pos = 0
                             OR SUBSTRING(@existing, @pos, LEN(@existing)) COLLATE Latin1_General_BIN2
                                <> SUBSTRING(@sql, CHARINDEX(@marker, @sql), LEN(@sql)) COLLATE Latin1_General_BIN2
                        THEN 1 ELSE 0 END;
        -- Q-17.slotmap: the view's rpt.ViewSlot rows must equal the wanted map (none for a view without routing); map and view
        -- are rewritten in ONE transaction (ALTER VIEW holds Sch-M until commit: no reader sees a view with a foreign map).
        SET @mapDiff = CASE WHEN EXISTS (SELECT ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1
                                         EXCEPT SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = @view)
                              OR EXISTS (SELECT ColumnDefId, Slot, Mo FROM rpt.ViewSlot WHERE ViewName = @view
                                         EXCEPT SELECT ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1)
                            THEN 1 ELSE 0 END;
        IF @ddl = 1 OR @mapDiff = 1
        BEGIN
            BEGIN TRANSACTION;
            IF @mapDiff = 1
            BEGIN
                DELETE FROM rpt.ViewSlot WHERE ViewName = @view;
                INSERT INTO rpt.ViewSlot (ViewName, ColumnDefId, Slot, Mo) SELECT @view, ColumnDefId, Slot, Mo FROM @slot WHERE @useSlots = 1;
            END
            IF @ddl = 1
                EXEC sys.sp_executesql @sql;
            COMMIT TRANSACTION;
        END

        IF @existing IS NULL
            INSERT INTO @result VALUES (@view, N'Created', @warning);
        ELSE IF @ddl = 1 OR @mapDiff = 1
            INSERT INTO @result VALUES (@view, N'Altered', @warning);
        ELSE
            INSERT INTO @result VALUES (@view, N'Unchanged', @warning);

next_group:
        FETCH NEXT FROM cur_views INTO @g, @header, @view;
    END
    CLOSE cur_views;
    DEALLOCATE cur_views;

    IF @clash IS NOT NULL
    BEGIN
        SET @message = CONCAT(
            N'rpt.usp_GenerateStableViews: the stable view name rpt.', @clash, N' collides with another view (', @clashWho,
            N'). Change the template, sheet or table code so that they differ by more than punctuation, case or a _v<N> suffix.');
        THROW 50409, @message, 1;
    END

    -- Q-17.slotmap: map rows of views that no longer exist are removed (full generation only).
    IF @TemplateVersionId IS NULL
        DELETE FROM rpt.ViewSlot WHERE OBJECT_ID(N'rpt.' + QUOTENAME(ViewName), N'V') IS NULL;

    IF @failure IS NOT NULL
        THROW 50422, @failure, 1;

    IF @TemplateVersionId IS NULL
    BEGIN TRY
        IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE class = 3 AND major_id = SCHEMA_ID(N'rpt') AND minor_id = 0 AND name = @fpName)
            EXEC sys.sp_updateextendedproperty @name = @fpName, @value = @fp, @level0type = N'SCHEMA', @level0name = N'rpt';
        ELSE
            EXEC sys.sp_addextendedproperty @name = @fpName, @value = @fp, @level0type = N'SCHEMA', @level0name = N'rpt';
    END TRY
    BEGIN CATCH
        SET @fp = @fp;
    END CATCH

    SELECT ViewName, Action, Warning FROM @result ORDER BY ViewName;
END;
GO

-- ══ PERMUSE: стабільні вʼюхи ДАНИХ ДОЗВОЛІВ для SSRS ═══════════════════════════
-- Дозвіл звʼязується з даними документів ЗНАЧЕННЯМ (поле шапки чи колонка Lookup → довідник дозволів). SSRS поєднує
-- «факт» (стабільна вʼюха таблиці документа, колонка `<Код>_Id`) з «лімітом» (вʼюхи нижче) за ідентифікатором дозволу,
-- НАЕ та речовини: ON a.<Дозвіл>_Id = s.PermitId AND a.<НАЕ>_Id = s.NaeId AND a.<Речовина>_Id = s.SubstanceId.
-- Ідентифікатори вʼюх — це Id записів довідників (ті самі, що `ValueRegistryEntryId` комірок і `<Код>_Id`).
--   rpt.v_Permit                  — шапка дозволу (один рядок на дозвіл будь-якого опублікованого типу);
--   rpt.v_PermitAirSource         — рядки AIR_SOURCES типу AIR (джерело/НАЕ дозволу; ✎ AIR-ON-AIR: таблиці перенесено з
--                                    окремого типу AIR_LIMITS у тип AIR; Region/Area — код запису довідника або текст,
--                                    RegionId/AreaId — Id запису (вид поля Registry/List), NULL для Text);
--   rpt.v_PermitAirSourceSubstance — рядки AIR_SOURCE_SUBSTANCES (ліміт речовини по НАЕ дозволу).
-- ⚠ Лише живі (не видалені логічно) записи; чернетки дозволів теж віддаються — відсіює звіт за `IsActive`/датами.
-- ⚠ `ValidTo` — ПЕРШИЙ НЕчинний день (півінтервал [ValidFrom, ValidTo)), як у застосунку.
CREATE OR ALTER VIEW rpt.v_Permit
AS
SELECT e.Id AS PermitId, pt.Code AS TypeCode, e.Code AS EntryCode,
       COALESCE(h.PermitNumber, e.Code) AS PermitNumber, h.Location, h.IssueDate,
       e.ValidFrom, e.ValidTo, h.ActualEnd, e.IsActive,
       h.Form300, h.Form301, h.Form302, h.Tox
  FROM cfg.PermitTypeDef AS pt
  JOIN dic.RegistryEntry AS e ON e.RegistryDefId = pt.HeaderRegistryDefId AND e.IsDeleted = 0
 OUTER APPLY (SELECT MAX(CASE WHEN f.Code = N'Permit_Number' THEN v.ValueString END) AS PermitNumber,
                     MAX(CASE WHEN f.Code = N'Location' THEN v.ValueString END) AS Location,
                     CAST(MAX(CASE WHEN f.Code = N'Issue_Date' THEN v.ValueDate END) AS date) AS IssueDate,
                     CAST(MAX(CASE WHEN f.Code = N'Actual_End_Date' THEN v.ValueDate END) AS date) AS ActualEnd,
                     CAST(MAX(CASE WHEN f.Code = N'Form_300' THEN CAST(v.ValueBool AS tinyint) END) AS bit) AS Form300,
                     CAST(MAX(CASE WHEN f.Code = N'Form_301' THEN CAST(v.ValueBool AS tinyint) END) AS bit) AS Form301,
                     CAST(MAX(CASE WHEN f.Code = N'Form_302' THEN CAST(v.ValueBool AS tinyint) END) AS bit) AS Form302,
                     CAST(MAX(CASE WHEN f.Code = N'TOx' THEN CAST(v.ValueBool AS tinyint) END) AS bit) AS Tox
                FROM dic.RegistryValue AS v
                JOIN cfg.RegistryFieldDef AS f ON f.Id = v.RegistryFieldDefId
               WHERE v.RegistryEntryId = e.Id) AS h
 WHERE pt.Status = 1;   -- Published
GO

CREATE OR ALTER VIEW rpt.v_PermitAirSource
AS
SELECT r.Id AS SourceId, a.PermitId, a.NaeId, nc.NaeCode,
       COALESCE(a.Region, re.Code) AS Region, COALESCE(a.Area, ae.Code) AS Area,
       a.SiteLabel, a.ProductionCode, a.SourceName,
       a.Quantity, a.HoursPerYear, a.PointName, a.SourceType, a.Height, a.Diameter, a.Velocity, a.Volume, a.Temperature,
       a.X1, a.Y1, a.X2, a.Y2,
       a.RegionId, a.AreaId
  FROM cfg.PermitTypeDef AS pt
  JOIN cfg.PermitTypeTable AS ptt ON ptt.PermitTypeDefId = pt.Id AND ptt.Code = N'AIR_SOURCES'
  JOIN dic.RegistryEntry AS r ON r.RegistryDefId = ptt.RegistryDefId AND r.IsDeleted = 0
 OUTER APPLY (SELECT MAX(CASE WHEN f.Code = N'PERMIT' THEN v.ValueRefEntryId END) AS PermitId,
                     MAX(CASE WHEN f.Code = N'NAE' THEN v.ValueRefEntryId END) AS NaeId,
                     MAX(CASE WHEN f.Code = N'REGION' THEN v.ValueString END) AS Region,
                     MAX(CASE WHEN f.Code = N'AREA' THEN v.ValueString END) AS Area,
                     MAX(CASE WHEN f.Code = N'REGION' THEN v.ValueRefEntryId END) AS RegionId,
                     MAX(CASE WHEN f.Code = N'AREA' THEN v.ValueRefEntryId END) AS AreaId,
                     MAX(CASE WHEN f.Code = N'SITE_LABEL' THEN v.ValueString END) AS SiteLabel,
                     MAX(CASE WHEN f.Code = N'PRODUCTION_CODE' THEN v.ValueString END) AS ProductionCode,
                     MAX(CASE WHEN f.Code = N'SOURCE_NAME' THEN v.ValueString END) AS SourceName,
                     MAX(CASE WHEN f.Code = N'QUANTITY' THEN v.ValueNumeric END) AS Quantity,
                     MAX(CASE WHEN f.Code = N'HOURS_PER_YEAR' THEN v.ValueNumeric END) AS HoursPerYear,
                     MAX(CASE WHEN f.Code = N'POINT_NAME' THEN v.ValueString END) AS PointName,
                     MAX(CASE WHEN f.Code = N'SOURCE_TYPE' THEN v.ValueString END) AS SourceType,
                     MAX(CASE WHEN f.Code = N'HEIGHT' THEN v.ValueNumeric END) AS Height,
                     MAX(CASE WHEN f.Code = N'DIAMETER' THEN v.ValueNumeric END) AS Diameter,
                     MAX(CASE WHEN f.Code = N'VELOCITY' THEN v.ValueNumeric END) AS Velocity,
                     MAX(CASE WHEN f.Code = N'VOLUME' THEN v.ValueNumeric END) AS Volume,
                     MAX(CASE WHEN f.Code = N'TEMPERATURE' THEN v.ValueNumeric END) AS Temperature,
                     MAX(CASE WHEN f.Code = N'X1' THEN v.ValueNumeric END) AS X1,
                     MAX(CASE WHEN f.Code = N'Y1' THEN v.ValueNumeric END) AS Y1,
                     MAX(CASE WHEN f.Code = N'X2' THEN v.ValueNumeric END) AS X2,
                     MAX(CASE WHEN f.Code = N'Y2' THEN v.ValueNumeric END) AS Y2
                FROM dic.RegistryValue AS v
                JOIN cfg.RegistryFieldDef AS f ON f.Id = v.RegistryFieldDefId
               WHERE v.RegistryEntryId = r.Id) AS a
 OUTER APPLY (SELECT MAX(nv.ValueString) AS NaeCode
                FROM dic.RegistryValue AS nv
                JOIN cfg.RegistryFieldDef AS nf ON nf.Id = nv.RegistryFieldDefId AND nf.Code = N'NaeCode'
               WHERE nv.RegistryEntryId = a.NaeId) AS nc
  LEFT JOIN dic.RegistryEntry AS re ON re.Id = a.RegionId
  LEFT JOIN dic.RegistryEntry AS ae ON ae.Id = a.AreaId
 WHERE pt.Code = N'AIR' AND pt.Status = 1;
GO

CREATE OR ALTER VIEW rpt.v_PermitAirSourceSubstance
AS
SELECT r.Id AS SourceSubstanceId, a.PermitId, a.SourceId, s.NaeId, s.NaeCode, a.SubstanceId, sc.SubstanceCode,
       a.GramsPerSecond, a.MgPerNm3, a.TonsPerYear
  FROM cfg.PermitTypeDef AS pt
  JOIN cfg.PermitTypeTable AS ptt ON ptt.PermitTypeDefId = pt.Id AND ptt.Code = N'AIR_SOURCE_SUBSTANCES'
  JOIN dic.RegistryEntry AS r ON r.RegistryDefId = ptt.RegistryDefId AND r.IsDeleted = 0
 OUTER APPLY (SELECT MAX(CASE WHEN f.Code = N'PERMIT' THEN v.ValueRefEntryId END) AS PermitId,
                     MAX(CASE WHEN f.Code = N'SOURCE' THEN v.ValueRefEntryId END) AS SourceId,
                     MAX(CASE WHEN f.Code = N'SUBSTANCE' THEN v.ValueRefEntryId END) AS SubstanceId,
                     MAX(CASE WHEN f.Code = N'GRAMS_PER_SECOND' THEN v.ValueNumeric END) AS GramsPerSecond,
                     MAX(CASE WHEN f.Code = N'MG_PER_NM3' THEN v.ValueNumeric END) AS MgPerNm3,
                     MAX(CASE WHEN f.Code = N'TONS_PER_YEAR' THEN v.ValueNumeric END) AS TonsPerYear
                FROM dic.RegistryValue AS v
                JOIN cfg.RegistryFieldDef AS f ON f.Id = v.RegistryFieldDefId
               WHERE v.RegistryEntryId = r.Id) AS a
  LEFT JOIN rpt.v_PermitAirSource AS s ON s.SourceId = a.SourceId
 OUTER APPLY (SELECT MAX(COALESCE(sv.ValueString, CONVERT(nvarchar(64), sv.ValueNumeric))) AS SubstanceCode
                FROM dic.RegistryValue AS sv
                JOIN cfg.RegistryFieldDef AS sf ON sf.Id = sv.RegistryFieldDefId AND sf.Code = N'SUBSTANCE_CODE'
               WHERE sv.RegistryEntryId = a.SubstanceId) AS sc
 WHERE pt.Code = N'AIR' AND pt.Status = 1;
GO

-- ══ (j5) rpt.v_DocumentCellsByPermit — числові комірки ЗА ДОЗВОЛОМ-ВЛАСНИКОМ (функція (j), людина 08.11) ═════════════════
-- Сирі дані для SSRS «факт за дозволом»: рядок вʼюхи = (комірка, дозвіл, якому зараховано її значення). Без агрегацій.
-- Лише числові (Int=1, Decimal=2) непорожні комірки таблиць, що мають колонку дозволу рядка (Lookup на опублікований тип
-- дозволу `cfg.PermitTypeDef.HeaderRegistryDefId` або групу `cfg.PermitGroup`, як `IPermitRegistryIndex`), або — коли такої
-- колонки немає — поле дозволу в шапці документа.
--   * Місяць ПОДІЛЕНО (усі частини місяця мають значення в `doc.CellPermitPart`) → рядок на частину: PermitId частини,
--     IsPart = 1, PartNo, DayFrom/DayTo, ValueNumeric частини. Частини ЗАМІНЮЮТЬ комірку (у комірці — їхня сума).
--   * Інакше → один рядок зі значенням комірки і дозволом, чинним в ОСТАННІЙ день місяця (колонка місяця: рік періоду +
--     `MonthNumber`; інша колонка: кінець періоду `doc.Period.PeriodEnd`): найпізніший `doc.PermitSegment` із
--     ValidFrom <= цього дня, інакше базовий дозвіл (значення комірки дозволу / поля шапки). Обчислені комірки не діляться
--     ніколи — так само дозвіл останнього дня.
-- Гарантія: SUM(ValueNumeric) у межах комірки = значення комірки (поділена — сума частин), без подвоєння.
-- Місяць комірки — як у застосунку (`SaveCellSplitsHandler`): рік із PeriodKey комірки, місяць — MonthNumber колонки, інакше
-- номер періоду. За Q-17 (@MonthRouting) місячні комірки лежать в екземплярі свого місяця: адреса частин і історії — логічна
-- (документ, таблиця, копія аркуша, RowKey), тож базовий дозвіл шукається спершу в тому самому рядку, далі — у рядку з тим
-- самим RowKey в інших екземплярах того ж року (найпізніший період).
-- ⚠ Лише живі дані `doc.*`; Status — колонка, як у rpt.v_DocumentCells. PermitSource: 'row' | 'header'.
CREATE OR ALTER VIEW rpt.v_DocumentCellsByPermit
AS
SELECT p.Id AS ProjectId, p.Code AS ProjectCode,
       d.Id AS DocumentId, d.BusinessKey AS DocumentKey,
       cv.PeriodKey,
       sd.Code AS SheetCode, CAST(COALESCE(dsh.CopyOrdinal, 1) AS smallint) AS SheetCopyOrdinal, td.Code AS TableCode,
       r.Id AS RowId, r.RowKey,
       cd.Code AS ColumnCode, cd.MonthNumber,
       CASE WHEN sp.IsSplit = 1 THEN pp.RegistryEntryId ELSE COALESCE(sg.RegistryEntryId, bs.BaseId) END AS PermitId,
       -- F13 p220 j5: the permit NUMBER as people know it (the entry's display, as the app names permits), not the
       -- registry code E000…; PermitId keeps the entry id.
       COALESCE(NULLIF(JSON_VALUE(pe.DisplayL10n, N'$.en'), N''), pe.Code) AS PermitCode,
       CAST(CASE WHEN rp.ColumnDefId IS NOT NULL THEN N'row' ELSE N'header' END AS nvarchar(6)) AS PermitSource,
       CAST(COALESCE(sp.IsSplit, 0) AS bit) AS IsPart,
       pp.PartNo, pp.DayFrom, pp.DayTo,
       CASE WHEN sp.IsSplit = 1 THEN pp.ValueNumeric ELSE cv.ValueNumeric END AS ValueNumeric,
       cv.IsCalculated,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS Status
FROM doc.CellValue          AS cv
JOIN doc.TableRow           AS r  ON r.PeriodKey = cv.PeriodKey AND r.Id = cv.TableRowId AND r.IsDeleted = 0
JOIN doc.TableInstance      AS ti ON ti.PeriodKey = r.PeriodKey AND ti.Id = r.TableInstanceId
JOIN doc.Document           AS d  ON d.Id = ti.DocumentId
JOIN doc.Project            AS p  ON p.Id = d.ProjectId
JOIN cfg.ColumnDef          AS cd ON cd.Id = cv.ColumnDefId AND cd.DataType IN (1, 2)
JOIN cfg.TableDef           AS td ON td.Id = ti.TableDefId
JOIN cfg.SheetDef           AS sd ON sd.Id = td.SheetDefId
LEFT JOIN wf.ApprovalState  AS a  ON a.DocumentId = d.Id AND a.SheetDefId = sd.Id AND a.PeriodKey = cv.PeriodKey
                                 AND (a.SheetCopyId = ti.SheetCopyId OR (a.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
LEFT JOIN doc.DocumentSheet AS dsh ON dsh.Id = ti.SheetCopyId
LEFT JOIN doc.Period        AS per ON per.ProjectId = d.ProjectId AND per.PeriodKey = cv.PeriodKey
-- Місяць комірки (NULL — номер поза 1..12: лише кінець періоду, без поділу).
CROSS APPLY (SELECT CASE WHEN COALESCE(cd.MonthNumber, cv.PeriodKey % 100) BETWEEN 1 AND 12
                         THEN DATEFROMPARTS(cv.PeriodKey / 100, COALESCE(cd.MonthNumber, cv.PeriodKey % 100), 1) END AS MonthStart) AS mo
CROSS APPLY (SELECT CASE WHEN cd.MonthNumber IS NOT NULL THEN EOMONTH(mo.MonthStart)
                         ELSE COALESCE(per.PeriodEnd, EOMONTH(mo.MonthStart)) END AS AsOf,
                    YEAR(mo.MonthStart) * 100 + MONTH(mo.MonthStart) AS MonthKey) AS dt
-- Колонка дозволу рядка: та, що має історію для цього рядка, інакше перша за порядком.
OUTER APPLY (SELECT TOP (1) pc.Id AS ColumnDefId
               FROM cfg.ColumnDef AS pc
              WHERE pc.TableDefId = td.Id AND pc.IsDeleted = 0 AND pc.DataType = 5
                AND (pc.LookupRegistryDefId IN (SELECT t.HeaderRegistryDefId FROM cfg.PermitTypeDef AS t WHERE t.Status = 1)
                     OR pc.LookupRegistryDefId IN (SELECT g.RegistryDefId FROM cfg.PermitGroup AS g))
              ORDER BY CASE WHEN EXISTS (SELECT 1 FROM doc.PermitSegment AS s
                                          WHERE s.DocumentId = d.Id AND s.TableDefId = td.Id AND s.ColumnDefId = pc.Id
                                            AND s.RowKey = r.RowKey
                                            AND (s.SheetCopyId = ti.SheetCopyId OR (s.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL)))
                            THEN 0 ELSE 1 END, pc.Ordinal, pc.Id) AS rp
-- Поле дозволу шапки — лише для таблиць БЕЗ колонки дозволу рядка: з історією, інакше перше за порядком.
OUTER APPLY (SELECT TOP (1) hf.Id AS HeaderFieldDefId, hv.ValueRegistryEntryId
               FROM doc.DocumentHeaderValue AS hv
               JOIN cfg.HeaderFieldDef AS hf ON hf.Id = hv.HeaderFieldDefId AND hf.IsDeleted = 0
              WHERE rp.ColumnDefId IS NULL AND hv.DocumentId = d.Id AND hv.IsEmpty = 0 AND hv.ValueRegistryEntryId IS NOT NULL
                AND (hf.LookupRegistryDefId IN (SELECT t.HeaderRegistryDefId FROM cfg.PermitTypeDef AS t WHERE t.Status = 1)
                     OR hf.LookupRegistryDefId IN (SELECT g.RegistryDefId FROM cfg.PermitGroup AS g))
              ORDER BY CASE WHEN EXISTS (SELECT 1 FROM doc.PermitSegment AS s
                                          WHERE s.DocumentId = d.Id AND s.HeaderFieldDefId = hf.Id) THEN 0 ELSE 1 END,
                       hf.Ordinal, hf.Id) AS hp
-- Базовий дозвіл: комірка дозволу того самого рядка, інакше рядка з тим самим RowKey в іншому екземплярі року (Q-17).
CROSS APPLY (SELECT CASE WHEN rp.ColumnDefId IS NULL THEN hp.ValueRegistryEntryId ELSE (
             SELECT TOP (1) b.ValueRegistryEntryId
               FROM doc.TableInstance AS bi
               JOIN doc.TableRow AS br ON br.PeriodKey = bi.PeriodKey AND br.TableInstanceId = bi.Id AND br.RowKey = r.RowKey
                                      AND br.IsDeleted = 0
               JOIN doc.CellValue AS b ON b.PeriodKey = br.PeriodKey AND b.TableRowId = br.Id AND b.ColumnDefId = rp.ColumnDefId
                                      AND b.IsEmpty = 0 AND b.ValueRegistryEntryId IS NOT NULL
              WHERE bi.DocumentId = d.Id AND bi.TableDefId = td.Id
                AND bi.PeriodKey BETWEEN (cv.PeriodKey / 100) * 100 AND (cv.PeriodKey / 100) * 100 + 99
                AND (bi.SheetCopyId = ti.SheetCopyId OR (bi.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
              ORDER BY CASE WHEN br.Id = r.Id AND br.PeriodKey = r.PeriodKey THEN 0 ELSE 1 END, bi.PeriodKey DESC) END AS BaseId) AS bs
-- Дозвіл, чинний в останній день місяця/періоду (найпізніша зміна історії з ValidFrom <= AsOf).
OUTER APPLY (SELECT TOP (1) s.RegistryEntryId
               FROM doc.PermitSegment AS s
              WHERE s.DocumentId = d.Id AND s.ValidFrom <= dt.AsOf
                AND ((rp.ColumnDefId IS NOT NULL AND s.TableDefId = td.Id AND s.ColumnDefId = rp.ColumnDefId AND s.RowKey = r.RowKey
                      AND (s.SheetCopyId = ti.SheetCopyId OR (s.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL)))
                     OR (rp.ColumnDefId IS NULL AND s.HeaderFieldDefId = hp.HeaderFieldDefId))
              ORDER BY s.ValidFrom DESC) AS sg
-- Поділ: дозволів у місяці = 1 + змін історії з датою всередині місяця (зміна з 1-го місяць не ділить); комірку поділено,
-- коли кожна з цих частин має значення (як `PermitHistoryReader.IsSplit`). Обчислені комірки не діляться.
OUTER APPLY (SELECT CASE WHEN cv.IsCalculated = 0 AND mo.MonthStart IS NOT NULL AND x.Spans >= 2
                          AND (SELECT COUNT(q.ValueNumeric)
                                 FROM doc.CellPermitPart AS q
                                WHERE q.PeriodKey = dt.MonthKey AND q.DocumentId = d.Id AND q.TableDefId = td.Id
                                  AND (q.SheetCopyId = ti.SheetCopyId OR (q.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
                                  AND q.RowKey = r.RowKey AND q.ColumnDefId = cv.ColumnDefId AND q.PartNo <= x.Spans) = x.Spans
                         THEN 1 ELSE 0 END AS IsSplit, x.Spans
               FROM (SELECT 1 + COUNT(*) AS Spans
                       FROM doc.PermitSegment AS s
                      WHERE s.DocumentId = d.Id AND s.ValidFrom > mo.MonthStart AND s.ValidFrom <= EOMONTH(mo.MonthStart)
                        AND ((rp.ColumnDefId IS NOT NULL AND s.TableDefId = td.Id AND s.ColumnDefId = rp.ColumnDefId
                              AND s.RowKey = r.RowKey
                              AND (s.SheetCopyId = ti.SheetCopyId OR (s.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL)))
                             OR (rp.ColumnDefId IS NULL AND s.HeaderFieldDefId = hp.HeaderFieldDefId))) AS x) AS sp
-- Частини поділеної комірки (для неподіленої — один порожній рядок OUTER APPLY: комірка цілою).
OUTER APPLY (SELECT q.PartNo, q.RegistryEntryId, q.DayFrom, q.DayTo, q.ValueNumeric
               FROM doc.CellPermitPart AS q
              WHERE sp.IsSplit = 1 AND q.PeriodKey = dt.MonthKey AND q.DocumentId = d.Id AND q.TableDefId = td.Id
                AND (q.SheetCopyId = ti.SheetCopyId OR (q.SheetCopyId IS NULL AND ti.SheetCopyId IS NULL))
                AND q.RowKey = r.RowKey AND q.ColumnDefId = cv.ColumnDefId AND q.PartNo <= sp.Spans) AS pp
LEFT JOIN dic.RegistryEntry AS pe ON pe.Id = CASE WHEN sp.IsSplit = 1 THEN pp.RegistryEntryId ELSE COALESCE(sg.RegistryEntryId, bs.BaseId) END
WHERE cv.IsEmpty = 0 AND cv.ValueNumeric IS NOT NULL
  AND (rp.ColumnDefId IS NOT NULL OR hp.HeaderFieldDefId IS NOT NULL);
GO

-- ⛔ Право читання для ОБЛІКОВОГО ЗАПИСУ SSRS — роль бази `rpt_reader`:
-- SELECT на всю схему `rpt` і нічого більше. Вʼюхи й таблиці `doc`/`cfg`/`wf`/
-- `uom` належать тому самому власнику (dbo), тож ланцюг власності не
-- рветься і прямого доступу до них роль не отримує. Нові вʼюхи генератора
-- право отримують автоматично — воно на схему, а не на об'єкт.
-- DBA додає до ролі обліковий запис джерела даних SSRS:
--     ALTER ROLE rpt_reader ADD MEMBER [DOMAIN\svc-ssrs];
IF DATABASE_PRINCIPAL_ID(N'rpt_reader') IS NULL
    CREATE ROLE rpt_reader;
GRANT SELECT ON SCHEMA::rpt TO rpt_reader;
GO

-- AN-10 (D-265, НФ-8.5): роль бази `ecr_viewer` — «бачить УСЕ» для довіреного
-- акаунта (DBA, діагностика). НЕ плутати з `rpt_reader` (SSRS, лише `rpt`).
-- Права: членство у `db_datareader` (SELECT на ВСІ схеми бази, зокрема майбутні)
-- + VIEW DEFINITION. Жодних ALTER/CREATE/INSERT/UPDATE/DELETE/EXECUTE.
-- Роль створюється ПОРОЖНЬОЮ; членство видає deploy-ecr.ps1 / setup-dev-db.ps1
-- параметром -ViewerAccount (або DBA вручну: ALTER ROLE ecr_viewer ADD MEMBER ...).
-- ⛔ Акаунт служби EcrApi/EcrWorker членом НЕ робити: це довірений ЧИТАЧ, а не
-- виконавець; службі потрібні власні права запису.
IF DATABASE_PRINCIPAL_ID(N'ecr_viewer') IS NULL
    CREATE ROLE ecr_viewer;
IF IS_ROLEMEMBER(N'db_datareader', N'ecr_viewer') = 0
    ALTER ROLE db_datareader ADD MEMBER ecr_viewer;
GRANT VIEW DEFINITION TO ecr_viewer;
GO
-- ⛔ Секрети — НЕ «дані звітності»: DENY має пріоритет над db_datareader.
-- Хеші паролів і штамп сесії користувачів, ключі кільця Data Protection (XML).
IF OBJECT_ID(N'sec.DataProtectionKey', N'U') IS NOT NULL
    DENY SELECT ON OBJECT::sec.DataProtectionKey TO ecr_viewer;
IF OBJECT_ID(N'sec.[User]', N'U') IS NOT NULL
    DENY SELECT ON OBJECT::sec.[User] (PasswordHash, SecurityStamp) TO ecr_viewer;
GO
