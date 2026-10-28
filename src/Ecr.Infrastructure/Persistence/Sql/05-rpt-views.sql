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
       CAST(COALESCE(a.Status, 0) AS tinyint) AS Status
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
WHERE cv.IsEmpty = 0;
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
CREATE OR ALTER PROCEDURE rpt.usp_GenerateTemplateViews
    @TemplateVersionId int = NULL,
    @MonthRouting bit = 0
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

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

    DECLARE @sheet int, @header nvarchar(400), @view sysname;
    DECLARE @select nvarchar(max), @selectText nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
    DECLARE @colId int, @colCode nvarchar(64), @colType tinyint, @alias nvarchar(16), @n int;
    -- Q-17.7: власність значень MonthsInColumns (місячний стовпець M{m} — в екземплярі періоду m,
    -- введений не-місячний — в екземплярі-якорі року). @monthMask: біт m — потрібен власник-місяць m.
    DECLARE @layout tinyint, @isMonth bit, @monthNo tinyint, @isInput bit, @monthMask int, @needAnchor bit,
            @ownerRows nvarchar(max), @ownerCells nvarchar(max), @valueCol nvarchar(128), @extra int, @mi int;
    -- Q-17.fix (B1): умовні агрегації значень власників - по одному OUTER APPLY на власника (OwnerNo: -1 власна копія,
    -- 0 якір року, 1..12 місяць), а не по приєднанню doc.CellValue на колонку.
    DECLARE @ownerRows2 nvarchar(max), @routed bit, @raw1 nvarchar(64), @agg nvarchar(max), @aggBody nvarchar(max), @ownerNo int, @aggList nvarchar(max), @idList nvarchar(max);
    DECLARE @pv TABLE (OwnerNo int NOT NULL, ColNo int NOT NULL, ColId int NOT NULL, Agg nvarchar(max) NOT NULL);
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
               @rvPack = N'', @rvChoose = N'', @rvExprs = N'', @plainAgg = N'', @plainCnt = 0;
        DELETE FROM @pv;
        SET @layout = (SELECT LayoutKind FROM cfg.TableDef WHERE Id = @id);
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
            SET @routed = CASE WHEN @MonthRouting = 1 AND @layout = 0 AND @colType NOT IN (6, 8)
                                    AND (@isMonth = 0 OR @monthNo BETWEEN 1 AND 12) THEN 1 ELSE 0 END;

            SET @numRef = @alias + N'.ValueNumeric';
            SET @strRef = @alias + N'.ValueString';
            IF @routed = 0 AND @MonthRouting = 1
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
                INSERT INTO @pv (OwnerNo, ColNo, ColId, Agg) VALUES
                    (-2, @n, @colId, @aggBody + N' AS cv' + CAST(@n AS nvarchar(10))),
                    (-1, @n, @colId, @agg);
                SET @famAgg = @famAgg + N', ' + CASE WHEN @colType = 3
                    THEN N'MAX(CAST(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' AND ' + CASE WHEN @isMonth = 1 THEN N'oi.PeriodKey % 100 = ' + CAST(@monthNo AS nvarchar(3)) ELSE N'oi.PeriodKey = pa.AnchorKey' END + N' THEN x.' + @raw1 + N' END AS tinyint))'
                    ELSE N'MAX(CASE WHEN x.ColumnDefId = ' + CAST(@colId AS nvarchar(10)) + N' AND ' + CASE WHEN @isMonth = 1 THEN N'oi.PeriodKey % 100 = ' + CAST(@monthNo AS nvarchar(3)) ELSE N'oi.PeriodKey = pa.AnchorKey' END + N' THEN x.' + @raw1 + N' END)' END
                    + N' AS v' + CAST(@n AS nvarchar(10));

                SET @valueCol = N'COALESCE(sc.cv' + CAST(@n AS nvarchar(10)) + N', '
                              + N'fam.v' + CAST(@n AS nvarchar(10)) + N', sv.v' + CAST(@n AS nvarchar(10)) + N')';
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
        IF @plainCnt > 0
        BEGIN
            SET @joins = @joins + N'
OUTER APPLY (SELECT ' + STUFF(@plainAgg, 1, 2, N'') + N' FROM doc.CellValue AS x WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id) AS pc';
            SET @extra = @extra + 1 - @plainCnt;   -- pc замість c<n> на кожну нероутовану колонку
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
            SET @ownerRows = N'
LEFT HASH JOIN (SELECT oi.DocumentId, oi.PeriodKey / 100 AS Yr, ow.RowKey' + @famAgg + N'
                  FROM doc.TableInstance AS oi
                  JOIN doc.Document AS od ON od.Id = oi.DocumentId
                  LEFT JOIN (SELECT ProjectId, PeriodKey / 100 AS Yr, MIN(PeriodKey) AS AnchorKey
                               FROM doc.Period GROUP BY ProjectId, PeriodKey / 100) AS pa
                         ON pa.ProjectId = od.ProjectId AND pa.Yr = oi.PeriodKey / 100
                  JOIN doc.TableRow AS ow ON ow.PeriodKey = oi.PeriodKey AND ow.TableInstanceId = oi.Id AND ow.IsDeleted = 0
                  JOIN doc.CellValue AS x ON x.PeriodKey = ow.PeriodKey AND x.TableRowId = ow.Id
                 WHERE oi.TableDefId = ' + CAST(@id AS nvarchar(10)) + N'
                 GROUP BY oi.DocumentId, oi.PeriodKey / 100, ow.RowKey) AS fam
       ON p.PeriodKind = 0 AND fam.DocumentId = ti.DocumentId AND fam.Yr = ti.PeriodKey / 100 AND fam.RowKey = r.RowKey';
            -- Власна копія (sv) і кожен власник (om<m>, omA) - по одному OUTER APPLY з умовною агрегацією (B1).
            -- Власна копія: рядок перегляду; без жодної введеної колонки з маршрутизацією цього блоку немає.
            SET @extra = @extra + 2 - (SELECT COUNT(*) FROM @pv WHERE OwnerNo = -1);   -- sv і sc замість c<n> на кожну колонку
            SET @ownerCells = N'';
            SET @ownerNo = -2;
            WHILE @ownerNo IS NOT NULL
            BEGIN
                -- ⚠ FOR XML PATH, а не «@var = @var + …» з ORDER BY: останнє не гарантує конкатенації всіх рядків.
                SET @aggList = STUFF((SELECT N', ' + Agg FROM @pv WHERE OwnerNo = @ownerNo ORDER BY ColNo
                                      FOR XML PATH(N''), TYPE).value(N'.', N'nvarchar(max)'), 1, 2, N'');
                -- P2 (T3_00 Land, 96 місячних колонок: 231 seek PK_CellValue на КОЖЕН рядок результату, ~5,4 млн виконань):
                -- власник читається ОДНИМ діапазоном ключа (PeriodKey, TableRowId) - усі комірки його рядка за один seek, -
                -- а потрібну колонку вибирає умовна агрегація MAX(CASE ColumnDefId ...) (вона й так фільтрує). Раніше тут стояв
                -- `x.ColumnDefId IN (<колонки власника>)`: оптимізатор розкривав його в seek НА КОЛОНКУ. Комірок у рядку власника
                -- небагато (розріджено), тож читання діапазону дешевше за N seek-ів; результат той самий.
                SET @idList = N'';
                SET @ownerCells = @ownerCells + N'
OUTER APPLY (SELECT ' + @aggList + N' FROM '
                    + CASE WHEN @ownerNo > 100
                           THEN N'doc.CellValue AS x WHERE x.PeriodKey = ow' + CAST(@ownerNo - 100 AS nvarchar(3)) + N'.PeriodKey AND x.TableRowId = ow' + CAST(@ownerNo - 100 AS nvarchar(3)) + N'.Id'
                           WHEN @ownerNo = -2
                           THEN N'doc.CellValue AS x WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id AND x.IsCalculated = 1'
                           WHEN @ownerNo = -1
                           THEN N'doc.CellValue AS x WHERE x.PeriodKey = r.PeriodKey AND x.TableRowId = r.Id'
                           ELSE N'doc.CellValue AS x WHERE x.PeriodKey = owA.PeriodKey AND x.TableRowId = owA.Id' + @idList END
                    + N') AS ' + CASE WHEN @ownerNo = -1 THEN N'sv' WHEN @ownerNo = -2 THEN N'sc' WHEN @ownerNo = 0 THEN N'omA' ELSE N'om' + CAST(@ownerNo - 100 AS nvarchar(3)) END;
                SET @ownerNo = (SELECT MIN(OwnerNo) FROM @pv WHERE OwnerNo > @ownerNo);
            END

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
                                          JOIN calc.CalculationResult AS cr ON cr.PeriodKey = ti.PeriodKey AND cr.DocumentId = d.Id AND cr.CalculationRunId = ru.Id
                                          INNER HASH JOIN (SELECT bb.Id, bb.ColumnDefId, bb.OutputCode, mv.Id AS MvId
                                                             FROM cfg.CalculationBinding AS bb
                                                             JOIN calc.MethodologyVersion AS mv ON mv.MethodologyId = bb.MethodologyId
                                                            WHERE bb.IsActive = 1 AND bb.TableDefId = ' + CAST(@id AS nvarchar(10)) + N' AND bb.ColumnDefId IN (' + STUFF(@rvIds, 1, 2, N'') + N')
                                                              AND REPLACE(bb.MatchJson, CHAR(32), N'''') = N''{}'') AS b
                                               ON b.MvId = cr.MethodologyVersionId AND b.OutputCode = cr.OutputCode
                                          WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current''
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
                                            WHERE ru.ProjectId = d.ProjectId AND ru.Status = N''Current''
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

        SET @sql = CONCAT(
N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
AS
', @marker, N': ', @header, N'. Не редагувати руками.
SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
       d.Id AS _DocumentId, d.BusinessKey AS _DocumentKey,
       ', @pkExpr, N' AS _PeriodKey,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS _Status,
       r.Id AS _RowId, r.RowKey AS _RowKey, r.Ordinal AS _RowOrdinal', @select, N'
', @fromSql, N'
LEFT JOIN wf.ApprovalState AS a ON a.DocumentId = d.Id AND a.SheetDefId = ', CAST(@sheet AS nvarchar(10)),
N' AND a.PeriodKey = ti.PeriodKey', @joins, N'
WHERE ti.TableDefId = ', CAST(@id AS nvarchar(10)), N';');

        -- ⚠ Порівнюється текст від позначки, а не весь: заголовок
        -- `CREATE OR ALTER VIEW` сервер не зобов'язаний зберігати дослівно.
        -- Вʼюха з тим самим ім'ям, зроблена руками (позначки немає), перезаписується.
        SET @existing = OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V'));
        SET @pos = CHARINDEX(@marker, @existing);

        IF @existing IS NULL
        BEGIN
            EXEC sys.sp_executesql @sql;
            INSERT INTO @result VALUES (@id, @view, N'Created');
        END
        ELSE IF @pos = 0
             OR SUBSTRING(@existing, @pos, LEN(@existing)) COLLATE Latin1_General_BIN2
                <> SUBSTRING(@sql, CHARINDEX(@marker, @sql), LEN(@sql)) COLLATE Latin1_General_BIN2
        BEGIN
            EXEC sys.sp_executesql @sql;
            INSERT INTO @result VALUES (@id, @view, N'Altered');
        END
        ELSE
        BEGIN
            INSERT INTO @result VALUES (@id, @view, N'Unchanged');
        END

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

    IF @failure IS NOT NULL
        THROW 50422, @failure, 1;

    SELECT TableDefId, N'rpt.' + ViewName AS ViewName, Action FROM @result ORDER BY TableDefId;
END;
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
