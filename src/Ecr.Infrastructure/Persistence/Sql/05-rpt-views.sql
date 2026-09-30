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
GO

-- ── Генератор пласких вʼюх rpt.v_<Звіт>_v<Версія> (ФВ-10.2, ФВ-10.4) ───────
-- Одна вʼюха на КОЖНУ опубліковану (і застарілу) версію звіту: колонка вʼюхи =
-- колонка версії (`ColumnsJson`), тип = тип значення в `rpt.ReportRow`
-- (text → ValueString, number → ValueNumeric, date → ValueDate).
--
-- ⛔ D-14/D-66: застосунок DDL НЕ виконує. Він лише ВИКЛИКАЄ цю процедуру
-- (при публікації версії й на старті), а DDL робить процедура від імені
-- ВЛАСНИКА (`EXECUTE AS OWNER`) — обліковий запис застосунку лишається без
-- прав на CREATE VIEW, потрібне йому лише EXECUTE на процедуру. Той самий
-- шлях, що й `arc.usp_EnsurePartitions`. DBA може викликати її і сам:
--     EXEC rpt.usp_GenerateReportViews;                  -- усі звіти
--     EXEC rpt.usp_GenerateReportViews @ReportDefId = 7; -- один звіт
--
-- ⚠ Версійність вʼюх (ФВ-10.12, D-53): ім'я несе ВЕРСІЮ, а опублікована версія
-- структурно незмінна (D-16). Тому нова версія звіту — НОВА вʼюха, а стара
-- лишається з тими самими колонками: RDL, прив'язаний до `_v1_0`, не ламається
-- від публікації `2.0`. Вʼюхи процедура не видаляє ніколи.
--
-- ⚠ Ідемпотентна: текст вʼюхи — чиста функція опису версії; якщо в базі вже
-- стоїть рівно такий самий, ALTER не виконується (немає Sch-M-блокування
-- посеред читання SSRS). Повторна публікація й повторний старт нічого не
-- змінюють.
--
-- ⚠ Предикат зрізу — за КОДОМ звіту й РЯДКОМ версії, не за Id: Id різні в
-- різних середовищах, а текст вʼюхи має бути однаковим у DEV і PROD.
--
-- ⛔ Фільтр статусу — у вʼюсі (ФВ-10.11, D-65): регуляторний звіт бачить лише
-- Approved/Submitted, решта — усі зрізи, включно з Draft (ФВ-10.10).
--
-- ⚠ Службові колонки мають префікс `Snapshot` (крім `RowNo`): колонка звіту
-- `PeriodKey` — законне поле джерела (`ReportSourceColumns`), і без префікса
-- вона зіткнулася б із періодом зрізу. Період рядка й період зрізу — різні
-- речі: річний зріз (`SnapshotPeriodKey` = NULL) несе місячні рядки.
CREATE OR ALTER PROCEDURE rpt.usp_GenerateReportViews
    @ReportDefId int = NULL
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @result TABLE (ReportVersionId int NOT NULL, ViewName sysname NOT NULL, Action nvarchar(16) NOT NULL);
    DECLARE @cols TABLE (Ordinal int NOT NULL, Code nvarchar(64) NULL, Kind nvarchar(16) NULL);
    DECLARE @versions TABLE (
        ReportVersionId int NOT NULL PRIMARY KEY,
        ReportDefId     int NOT NULL,
        ReportCode      nvarchar(64) NOT NULL,
        [Version]       nvarchar(20) NOT NULL,
        IsRegulatory    bit NOT NULL,
        ColumnsJson     nvarchar(max) NOT NULL,
        ViewName        sysname NULL);

    -- ⚠ Імена рахуються для ВСІХ опублікованих версій, а не лише для звіту
    -- з параметра: зіткнення імені може бути й між різними звітами
    -- (код `A_v1` версії `2` і код `A` версії `1_v2` → `v_A_v1_v2`).
    INSERT INTO @versions (ReportVersionId, ReportDefId, ReportCode, [Version], IsRegulatory, ColumnsJson)
    SELECT v.Id, d.Id, d.Code, v.[Version], d.IsRegulatory, v.ColumnsJson
      FROM rpt.ReportVersion AS v
      JOIN rpt.ReportDef     AS d ON d.Id = v.ReportDefId
     WHERE v.Status IN (1, 2);   -- Published, Deprecated: застаріла версія вʼюху НЕ втрачає

    -- Ім'я: v_<Код>_v<Версія>, де все, що не [A-Za-z0-9_], стає `_` ("1.0" → "1_0").
    DECLARE @id int, @raw nvarchar(200), @pos int;
    DECLARE cur_names CURSOR LOCAL FAST_FORWARD FOR SELECT ReportVersionId FROM @versions;
    OPEN cur_names;
    FETCH NEXT FROM cur_names INTO @id;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SELECT @raw = N'v_' + ReportCode + N'_v' + [Version] FROM @versions WHERE ReportVersionId = @id;
        SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        WHILE @pos > 0
        BEGIN
            SET @raw = STUFF(@raw, @pos, 1, N'_');
            SET @pos = PATINDEX(N'%[^A-Za-z0-9_]%', @raw COLLATE Latin1_General_BIN2);
        END
        UPDATE @versions SET ViewName = @raw WHERE ReportVersionId = @id;
        FETCH NEXT FROM cur_names INTO @id;
    END
    CLOSE cur_names;
    DEALLOCATE cur_names;

    DELETE FROM @versions WHERE @ReportDefId IS NOT NULL AND ReportDefId <> @ReportDefId
                            AND ViewName NOT IN (SELECT t.ViewName FROM @versions AS t WHERE t.ReportDefId = @ReportDefId);

    -- ⛔ Дві версії з одним ім'ям ("1.0" і "1_0") переписували б одна одній
    -- вʼюху по черзі — RDL бачив би то одну, то іншу. Відмова, а не вибір:
    -- жодна з двох вʼюху не отримує, решта генерується, а наприкінці —
    -- помилка з іменем. Порівняння — зіставленням бази, тобто так само, як
    -- сервер порівнює імена об'єктів. ⚠ Решта генерується саме тому, що
    -- виклик без параметра йде на старті: одна невдала пара версій не має
    -- лишати без вʼюх усі інші звіти.
    DECLARE @clash sysname = (SELECT TOP (1) ViewName FROM @versions GROUP BY ViewName HAVING COUNT(*) > 1);
    DELETE FROM @versions
     WHERE ViewName IN (SELECT t.ViewName FROM @versions AS t GROUP BY t.ViewName HAVING COUNT(*) > 1);

    DELETE FROM @versions WHERE @ReportDefId IS NOT NULL AND ReportDefId <> @ReportDefId;

    DECLARE @code nvarchar(64), @version nvarchar(20), @regulatory bit, @columns nvarchar(max), @view sysname;
    DECLARE @select nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
    DECLARE @bad nvarchar(200), @count int, @failure nvarchar(2048);
    DECLARE @ordinal int, @colCode nvarchar(64), @colKind nvarchar(16), @alias nvarchar(16);

    DECLARE cur_views CURSOR LOCAL FAST_FORWARD FOR
        SELECT ReportVersionId, ReportCode, [Version], IsRegulatory, ColumnsJson, ViewName
          FROM @versions ORDER BY ReportVersionId;
    OPEN cur_views;
    FETCH NEXT FROM cur_views INTO @id, @code, @version, @regulatory, @columns, @view;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        DELETE FROM @cols;
        SET @message = NULL;

        -- ⚠ Зламаний опис однієї версії — не причина лишити без вʼюх усі інші
        -- (виклик без параметра йде на старті): версія пропускається, перша
        -- причина запам'ятовується і стає помилкою процедури наприкінці.
        IF ISJSON(@columns) = 0 OR LEFT(LTRIM(@columns), 1) <> N'['
           OR EXISTS (SELECT 1 FROM OPENJSON(@columns) AS e WHERE e.[type] <> 5)
        BEGIN
            SET @message = CONCAT(N'rpt.usp_GenerateReportViews: ColumnsJson версії ', @id, N' не є масивом об''єктів JSON.');
            GOTO next_version;
        END

        INSERT INTO @cols (Ordinal, Code, Kind)
        SELECT CAST(j.[key] AS int), c.code, c.kind
          FROM OPENJSON(@columns) AS j
         CROSS APPLY OPENJSON(j.[value]) WITH (code nvarchar(64) '$.code', kind nvarchar(16) '$.kind') AS c;

        SET @count = (SELECT COUNT(*) FROM @cols);

        -- ⛔ Кожна перевірка нижче — відмова версії з іменем винного, а не
        -- «пропущу колонку»: вʼюха, що мовчки не має колонки держформи, гірша за
        -- відсутню, бо RDL упаде аж у SSRS, а не тут.
        IF @count = 0
        BEGIN
            SET @message = CONCAT(N'rpt.usp_GenerateReportViews: версія ', @id, N' не має колонок.');
            GOTO next_version;
        END

        -- Ліміт SQL Server — 256 таблиць у запиті; вʼюха бере 2 + по одній на колонку.
        IF @count > 250
        BEGIN
            SET @message = CONCAT(N'rpt.usp_GenerateReportViews: версія ', @id, N' має ', @count,
                                  N' колонок, вʼюха вміщує щонайбільше 250.');
            GOTO next_version;
        END

        SET @bad = (SELECT TOP (1) COALESCE(Code, N'(без коду)') FROM @cols
                     WHERE Code IS NULL OR Code COLLATE Latin1_General_BIN2 LIKE N'%[^A-Za-z0-9_]%'
                        OR Kind IS NULL OR Kind NOT IN (N'text', N'number', N'date'));
        IF @bad IS NOT NULL
        BEGIN
            SET @message = CONCAT(N'rpt.usp_GenerateReportViews: колонка «', @bad, N'» версії ', @id,
                                  N' має недопустимий код або тип.');
            GOTO next_version;
        END

        -- ⛔ Зіставлення бази нечутливе до регістру: `Value` і `value` — ОДНА
        -- колонка вʼюхи. І код не може збігатися зі службовою колонкою.
        SET @bad = (SELECT TOP (1) Code FROM @cols
                     WHERE UPPER(Code) IN (N'SNAPSHOTID', N'SNAPSHOTPROJECTID', N'SNAPSHOTPERIODKEY', N'SNAPSHOTSTATUS',
                                           N'SNAPSHOTBUILTAT', N'SNAPSHOTCALCULATIONRUNID', N'ROWNO')
                        OR UPPER(Code) IN (SELECT UPPER(c2.Code) FROM @cols AS c2 GROUP BY UPPER(c2.Code)
                                            HAVING COUNT(*) > 1));
        IF @bad IS NOT NULL
        BEGIN
            SET @message = CONCAT(N'rpt.usp_GenerateReportViews: колонка «', @bad, N'» версії ', @id,
                                  N' збігається зі службовою колонкою вʼюхи або з іншою колонкою без урахування регістру.');
            GOTO next_version;
        END

        -- ⚠ Цикл, а не `STRING_AGG`: підлога сервера — 2016 SP1 (`D-101`),
        -- `STRING_AGG` там немає (та сама причина, що в `01-filegroups.sql`).
        SELECT @select = N'', @joins = N'', @ordinal = MIN(Ordinal) FROM @cols;
        WHILE @ordinal IS NOT NULL
        BEGIN
            SELECT @colCode = Code, @colKind = Kind FROM @cols WHERE Ordinal = @ordinal;
            SET @alias = N'c' + CAST(@ordinal AS nvarchar(10));
            SET @select = @select + N',
       ' + @alias + N'.'
                + CASE @colKind WHEN N'text' THEN N'ValueString' WHEN N'number' THEN N'ValueNumeric' ELSE N'ValueDate' END
                + N' AS ' + QUOTENAME(@colCode);
            SET @joins = @joins + N'
LEFT JOIN rpt.ReportRow AS ' + @alias + N' ON ' + @alias + N'.SnapshotId = k.SnapshotId AND '
                + @alias + N'.RowNo = k.RowNo AND ' + @alias + N'.ColumnCode = N''' + @colCode + N'''';
            SET @ordinal = (SELECT MIN(Ordinal) FROM @cols WHERE Ordinal > @ordinal);
        END

        SET @sql = CONCAT(
N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
AS
-- Згенеровано rpt.usp_GenerateReportViews: звіт ', REPLACE(REPLACE(@code, NCHAR(13), N' '), NCHAR(10), N' '), N', версія ', REPLACE(REPLACE(@version, NCHAR(13), N' '), NCHAR(10), N' '), N'. Не редагувати руками.
SELECT s.Id AS SnapshotId, s.ProjectId AS SnapshotProjectId, s.PeriodKey AS SnapshotPeriodKey,
       s.Status AS SnapshotStatus, s.BuiltAt AS SnapshotBuiltAt, s.CalculationRunId AS SnapshotCalculationRunId,
       k.RowNo', @select, N'
FROM rpt.ReportSnapshot AS s
JOIN rpt.ReportVersion  AS v ON v.Id = s.ReportVersionId
JOIN rpt.ReportDef      AS d ON d.Id = v.ReportDefId
JOIN (SELECT r.SnapshotId, r.RowNo FROM rpt.ReportRow AS r GROUP BY r.SnapshotId, r.RowNo) AS k
  ON k.SnapshotId = s.Id', @joins, N'
WHERE d.Code = N''', REPLACE(@code, N'''', N''''''), N''' AND v.[Version] = N''', REPLACE(@version, N'''', N''''''), N'''
  AND s.IsCurrent = 1
  AND s.Status IN ', CASE WHEN @regulatory = 1 THEN N'(1, 2)' ELSE N'(0, 1, 2)' END, N';');

        -- ⚠ Порівнюється тіло від позначки «Згенеровано», а не весь текст:
        -- заголовок `CREATE OR ALTER VIEW` сервер не зобов'язаний зберігати
        -- дослівно, а тіло — зберігає. Вʼюха з тим самим ім'ям, зроблена
        -- руками (позначки немає), перезаписується.
        SET @existing = OBJECT_DEFINITION(OBJECT_ID(N'rpt.' + QUOTENAME(@view), N'V'));
        SET @pos = CHARINDEX(N'-- Згенеровано rpt.usp_GenerateReportViews', @existing);
        IF @pos > 0
            SET @existing = SUBSTRING(@existing, @pos, LEN(@existing));

        IF @existing IS NULL
        BEGIN
            EXEC sys.sp_executesql @sql;
            INSERT INTO @result VALUES (@id, @view, N'Created');
        END
        ELSE IF @pos = 0 OR @existing COLLATE Latin1_General_BIN2
                <> SUBSTRING(@sql, CHARINDEX(N'-- Згенеровано rpt.usp_GenerateReportViews', @sql), LEN(@sql)) COLLATE Latin1_General_BIN2
        BEGIN
            EXEC sys.sp_executesql @sql;
            INSERT INTO @result VALUES (@id, @view, N'Altered');
        END
        ELSE
        BEGIN
            INSERT INTO @result VALUES (@id, @view, N'Unchanged');
        END

next_version:
        IF @message IS NOT NULL AND @failure IS NULL
            SET @failure = @message;

        FETCH NEXT FROM cur_views INTO @id, @code, @version, @regulatory, @columns, @view;
    END
    CLOSE cur_views;
    DEALLOCATE cur_views;

    IF @clash IS NOT NULL
    BEGIN
        SET @message = CONCAT(
            N'rpt.usp_GenerateReportViews: дві опубліковані версії дають одне ім''я вʼюхи rpt.', @clash,
            N'. Змініть код звіту або рядок версії так, щоб вони відрізнялися не лише розділовими знаками чи регістром.');
        THROW 50409, @message, 1;
    END

    IF @failure IS NOT NULL
        THROW 50422, @failure, 1;

    SELECT ReportVersionId, N'rpt.' + ViewName AS ViewName, Action FROM @result ORDER BY ReportVersionId;
END;
GO
