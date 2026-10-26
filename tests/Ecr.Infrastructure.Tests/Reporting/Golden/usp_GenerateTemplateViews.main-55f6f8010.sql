-- GOLDEN: процедура rpt.usp_GenerateTemplateViews з origin/main 55f6f8010 (до Q-17.7), БЕЗ змін, окрім імені (_GoldenMain).
-- Еталон для ReportViewMonthOwnershipTests: з вимкненим прапором нова процедура мусить давати ТОЙ САМИЙ текст вʼюх.
CREATE OR ALTER PROCEDURE rpt.usp_GenerateTemplateViews_GoldenMain
    @TemplateVersionId int = NULL
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
    DECLARE @select nvarchar(max), @joins nvarchar(max), @sql nvarchar(max), @existing nvarchar(max), @message nvarchar(2048);
    DECLARE @colId int, @colCode nvarchar(64), @colType tinyint, @alias nvarchar(16), @n int;
    DECLARE @marker nvarchar(64) = N'-- Згенеровано rpt.usp_GenerateTemplateViews';
    DECLARE @failure nvarchar(2048);

    DECLARE cur_views CURSOR LOCAL FAST_FORWARD FOR
        SELECT TableDefId, SheetDefId, Header, ViewName FROM @tables ORDER BY TableDefId;
    OPEN cur_views;
    FETCH NEXT FROM cur_views INTO @id, @sheet, @header, @view;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- ⚠ Цикл, а не `STRING_AGG`: підлога сервера — 2016 SP1 (`D-101`).
        SELECT @select = N'', @joins = N'', @n = 0;
        SET @colId = (SELECT MIN(Id) FROM cfg.ColumnDef WHERE TableDefId = @id AND IsDeleted = 0);
        WHILE @colId IS NOT NULL
        BEGIN
            SELECT @colCode = Code, @colType = DataType FROM cfg.ColumnDef WHERE Id = @colId;
            SET @n = @n + 1;
            SET @alias = N'c' + CAST(@n AS nvarchar(10));
            SET @select = @select + N',
       ' + @alias + N'.'
                + CASE @colType
                      WHEN 0 THEN N'ValueString'
                      WHEN 3 THEN N'ValueBool'
                      WHEN 4 THEN N'ValueDate'
                      WHEN 5 THEN N'ValueRegistryEntryId'
                      WHEN 7 THEN N'ValueUnitId'
                      ELSE N'ValueNumeric'
                  END
                + N' AS ' + QUOTENAME(@colCode);
            SET @joins = @joins + N'
LEFT JOIN doc.CellValue AS ' + @alias + N' ON ' + @alias + N'.PeriodKey = r.PeriodKey AND '
                + @alias + N'.TableRowId = r.Id AND ' + @alias + N'.ColumnDefId = ' + CAST(@colId AS nvarchar(10));
            SET @colId = (SELECT MIN(Id) FROM cfg.ColumnDef WHERE TableDefId = @id AND IsDeleted = 0 AND Id > @colId);
        END

        -- ⚠ Колонки — у порядку `Id`, а не `Ordinal`: `Ordinal` — лише
        -- відображення (D-17), і перестановка колонок у новій версії не має
        -- міняти порядок колонок уже опублікованої вʼюхи. Ліміт SQL Server —
        -- 256 таблиць у запиті: 5 службових + по одній на колонку.
        IF @n > 250
        BEGIN
            -- ⚠ Не THROW тут: на старті (виклик без параметра) одна задовга
            -- таблиця не має лишати без вʼюх усі інші. Помилка — наприкінці.
            -- ⚠ У повідомленні — заголовок (шаблон, версія, аркуш, таблиця): його
            -- читає /health/ready і журнал публікації, там голий Id нічого не каже.
            SET @failure = COALESCE(@failure, CONCAT(N'rpt.usp_GenerateTemplateViews: ', @header, N' (таблиця ', @id,
                                  N') має ', @n, N' колонок, вʼюха вміщує щонайбільше 250.'));
            GOTO next_table;
        END

        SET @sql = CONCAT(
N'CREATE OR ALTER VIEW rpt.', QUOTENAME(@view), N'
AS
', @marker, N': ', @header, N'. Не редагувати руками.
SELECT p.Id AS _ProjectId, p.Code AS _ProjectCode,
       d.Id AS _DocumentId, d.BusinessKey AS _DocumentKey,
       r.PeriodKey AS _PeriodKey,
       CAST(COALESCE(a.Status, 0) AS tinyint) AS _Status,
       r.Id AS _RowId, r.RowKey AS _RowKey, r.Ordinal AS _RowOrdinal', @select, N'
FROM doc.TableInstance AS ti
JOIN doc.TableRow      AS r ON r.PeriodKey = ti.PeriodKey AND r.TableInstanceId = ti.Id AND r.IsDeleted = 0
JOIN doc.Document      AS d ON d.Id = ti.DocumentId
JOIN doc.Project       AS p ON p.Id = d.ProjectId
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
