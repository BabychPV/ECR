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

-- src/Ecr.Infrastructure/Persistence/Sql/04-partition-maintenance.sql
-- Працює НА ВИПЕРЕДЖЕННЯ: SPLIT порожньої останньої партиції — операція
-- метаданих; SPLIT непорожньої переміщує дані з блокуванням.
--
-- ⛔ U1-02 (аудит 09.10c). SPLIT `pf_ByPeriodKey` бере Sch-M на КОЖНУ таблицю
--    схеми `ps_ByPeriodKey` (doc.CellValue/TableRow/TableInstance, calc.*,
--    ext.RowWindowValue). Sch-M несумісне навіть із Sch-S читачів під RCSI, і
--    запит на нього стає в чергу за найдовшим читачем (нічні перевірки о 02:15,
--    довгий звіт SSRS), а ЗА НИМ у черзі — усі нові запити до цих таблиць.
--    Без межі очікування (дефолт `LOCK_TIMEOUT = -1`) черга тримала б
--    користувачів стільки, скільки триває найдовший читач.
--
-- ⚠ Тому кожна спроба SPLIT чекає не довше @LockTimeoutMs (за замовчуванням
--    5 с — менше за 8 с, після яких PATCH комірки віддає 409, N-3), а 1222
--    (вичерпано очікування) або 1205 (дедлок) — не провал, а пауза
--    @RetryDelaySeconds і нова спроба, до @MaxAttempts разів. Між спробами
--    Sch-M не запитується, тож користувачі проходять. Інша помилка — одразу.
--
-- ⚠ LOCK_TIMEOUT ставиться в ДИНАМІЧНОМУ пакеті разом із самим SPLIT: так він
--    діє рівно на DDL і не змінює сесію викликача (Agent, DBA вручну).
--
-- ⚠ Слід — `itg.MaintenanceRun` і на успіх (`Succeeded`, скільки меж додано і
--    скільки було повторів), і на провал (`Failed` з номером помилки), після
--    чого помилка йде далі (крок Agent — `Failed`). Раніше рядок писався лише на
--    успіх і зі статусом `Completed`, якого зведення збоїв `NotificationJob`
--    (`Status != "Succeeded"`) не відрізняло від провалу.
--
-- ⚠ @Today — для тестів і ручного запуску «на дату»; NULL = поточна дата UTC
--    (як і в `usp_EnsureAuditPartitions`, якій він передається далі).
--
-- ✎ PSS-09 (аудит 20.11; рішення людини 20.11 №6 — межі для поточного року):
--  * діапазон — УСІ місяці поточного року Y (від січня), наступного року Y+1 і січень Y+2
--    (він закриває грудень Y+1), а також @MonthsAhead місяців уперед, якщо це далі.
--    Раніше бралися лише @MonthsAhead місяців від наступного: місяці поточного року, яких
--    чомусь немає (база розгорнута пізно, завдання Agent не працювало), не додавались ніколи,
--    а грудень наступного року ділив крайню праву партицію з усім майбутнім;
--  * SPLIT партиції, у якій уже є рядки (будь-яка таблиця на ps_ByPeriodKey, купа чи
--    кластерний індекс), НЕ виконується: це переміщення даних під Sch-M на всіх таблицях
--    схеми. Межа пропускається, прогін пише `Degraded` зі списком `refused` і причиною;
--    решта меж додається як звичайно. Розв'язка — вручну у вікні обслуговування (runbook);
--    `PartitionCheckJob` окремо сигналізує про рядки в крайніх партиціях.
CREATE OR ALTER PROCEDURE arc.usp_EnsurePartitions
    @MonthsAhead       int  = 6,
    @Today             date = NULL,
    @LockTimeoutMs     int  = 5000,
    @MaxAttempts       int  = 10,
    @RetryDelaySeconds int  = 60
AS
BEGIN
    SET NOCOUNT ON;

    IF @LockTimeoutMs IS NULL OR @LockTimeoutMs < 1
       OR @MaxAttempts IS NULL OR @MaxAttempts < 1
       OR @RetryDelaySeconds IS NULL OR @RetryDelaySeconds < 0 OR @RetryDelaySeconds > 3600
        THROW 50017, N'@LockTimeoutMs і @MaxAttempts мають бути додатними, @RetryDelaySeconds — від 0 до 3600.', 1;

    DECLARE @started datetime2(3) = SYSUTCDATETIME();
    DECLARE @t date = COALESCE(@Today, CAST(GETUTCDATE() AS date));
    -- PSS-09: від січня поточного року до пізнішого з «січень Y+2» і «поточний місяць + @MonthsAhead».
    DECLARE @d date = DATEFROMPARTS(YEAR(@t), 1, 1);
    DECLARE @last date = DATEADD(MONTH, COALESCE(@MonthsAhead, 0), DATEFROMPARTS(YEAR(@t), MONTH(@t), 1));
    IF @last < DATEFROMPARTS(YEAR(@t) + 2, 1, 1)
        SET @last = DATEFROMPARTS(YEAR(@t) + 2, 1, 1);
    DECLARE @key int, @sql nvarchar(600), @p int, @rows bigint;
    DECLARE @attempt int, @done bit, @added int = 0, @retries int = 0;
    DECLARE @refused nvarchar(max) = N'';
    DECLARE @delay char(8) =
        CONVERT(char(8), DATEADD(SECOND, @RetryDelaySeconds, CAST('00:00:00' AS time(0))), 108);

    BEGIN TRY
        WHILE @d <= @last
        BEGIN
            SET @key = YEAR(@d) * 100 + MONTH(@d);

            -- PSS-09: межа всередині партиції з рядками — не SPLIT (переміщення даних під Sch-M).
            -- Рядки рахуються за sys.partitions (купа / кластерний індекс кожної таблиці на
            -- ps_ByPeriodKey, включно з arc.*): метадані, без читання таблиць.
            SET @rows = -1;   -- -1: межа вже є
            IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values rv
                           JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                           WHERE pf.name = N'pf_ByPeriodKey' AND CAST(rv.value AS int) = @key)
            BEGIN
                SET @p = $PARTITION.pf_ByPeriodKey(@key);
                SELECT @rows = ISNULL(SUM(prt.rows), 0)
                  FROM sys.partitions AS prt
                  JOIN sys.indexes AS ix ON ix.object_id = prt.object_id AND ix.index_id = prt.index_id
                  JOIN sys.partition_schemes AS ps ON ps.data_space_id = ix.data_space_id
                 WHERE ps.name = N'ps_ByPeriodKey' AND ix.index_id IN (0, 1) AND prt.partition_number = @p;
            END

            IF @rows > 0
                SET @refused = @refused + N',' + CAST(@key AS nvarchar(10));
            ELSE IF @rows = 0
            BEGIN
                SET @sql = N'SET LOCK_TIMEOUT ' + CAST(@LockTimeoutMs AS nvarchar(10)) + N'; '
                         + N'ALTER PARTITION SCHEME ps_ByPeriodKey NEXT USED [DATA_HOT]; '
                         + N'ALTER PARTITION FUNCTION pf_ByPeriodKey() SPLIT RANGE ('
                         + CAST(@key AS nvarchar(10)) + N');';
                SET @attempt = 1;
                SET @done = 0;

                WHILE @done = 0
                BEGIN
                    BEGIN TRY
                        EXEC sp_executesql @sql;
                        SET @done = 1;
                        SET @added = @added + 1;
                    END TRY
                    BEGIN CATCH
                        IF ERROR_NUMBER() NOT IN (1222, 1205) OR @attempt >= @MaxAttempts
                            THROW;

                        SET @attempt = @attempt + 1;
                        SET @retries = @retries + 1;
                        IF @RetryDelaySeconds > 0
                            WAITFOR DELAY @delay;
                    END CATCH
                END
            END

            SET @d = DATEADD(MONTH, 1, @d);
        END

        -- Аудит: межі pf_AuditByMonth - на 12 місяців уперед (окрема функція, окрема процедура).
        EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = 12, @Today = @Today;

        -- PSS-09: пропущена межа — Degraded (зведення збоїв бере Status <> Succeeded), не провал кроку.
        INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
        VALUES (N'EnsurePartitions', @started, SYSUTCDATETIME(),
                CASE WHEN @refused = N'' THEN N'Succeeded' ELSE N'Degraded' END,
                N'{"added":' + CAST(@added AS nvarchar(10))
                + N',"retries":' + CAST(@retries AS nvarchar(10))
                + CASE WHEN @refused = N'' THEN N''
                       ELSE N',"refused":[' + STUFF(@refused, 1, 1, N'')
                            + N'],"reason":"партиція з рядками: SPLIT переміщував би дані під Sch-M; межу додати вручну у вікні обслуговування"'
                  END
                + N'}');
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK;

        INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
        VALUES (N'EnsurePartitions', @started, SYSUTCDATETIME(), N'Failed',
                N'{"error":' + CAST(ERROR_NUMBER() AS nvarchar(10))
                + N',"key":' + ISNULL(CAST(@key AS nvarchar(10)), N'null')
                + N',"attempts":' + ISNULL(CAST(@attempt AS nvarchar(10)), N'0')
                + N',"added":' + CAST(@added AS nvarchar(10))
                + N',"message":"' + STRING_ESCAPE(LEFT(ERROR_MESSAGE(), 500), N'json') + N'"}');

        THROW;
    END CATCH
END;
GO

-- Автообслуговування `pf_AuditByMonth`: межі (перші дні місяців) тримаються
-- щонайменше на @MonthsAhead місяців уперед від @Today (за замовчуванням —
-- поточна дата UTC). До цього межі закінчувалися 2027-06-01 і ніхто їх не
-- довантажував: після червня 2027 аудит мовчки йшов би в останню партицію.
--
-- ⚠ Ідемпотентна: додає лише ВІДСУТНІ межі; повторний виклик нічого не змінює,
-- дубль межі неможливий (перевірка за sys.partition_range_values). Межі, що вже
-- далеко попереду, не чіпає. Якщо функції немає (БД без партиціонування) —
-- нічого не робить і повертає @Added = 0.
--
-- ⚠ Безпека для даних: SPLIT RANGE розбиває партицію, що ВЖЕ містить межу з
-- правого боку. Для нових місяців це крайня права партиція, у якій немає рядків
-- (ChangedAt — момент запису, майбутніх дат немає), тож SPLIT — операція
-- метаданих: дані не переміщуються. Утримує SCH-M на таблицях, що лежать на
-- ps_AuditByMonth, лише на мить; `LOCK_TIMEOUT` не дає чекати вічно за довгою
-- транзакцією (виняток 1222 — прогін помилкою, повторить наступна ніч).
-- Партицію з даними (межа ВСЕРЕДИНІ вже заповненого діапазону) процедура сама
-- не створює: вона йде лише вперед від поточного місяця.
--
-- ⚠ WITH EXECUTE AS OWNER: обліковому запису застосунку DDL-прав не потрібно
-- (D-66) — потрібне лише право EXECUTE на процедуру; ALTER PARTITION FUNCTION
-- виконується від імені власника схеми (dbo). Тригери незмінності aud.* на DDL
-- не реагують.
CREATE OR ALTER PROCEDURE arc.usp_EnsureAuditPartitions
    @MonthsAhead int = 12,
    @Today date = NULL,
    @Added int = NULL OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET LOCK_TIMEOUT 30000;
    SET @Added = 0;

    IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
        RETURN;

    DECLARE @first date = DATEFROMPARTS(YEAR(COALESCE(@Today, CAST(GETUTCDATE() AS date))),
                                        MONTH(COALESCE(@Today, CAST(GETUTCDATE() AS date))), 1);
    DECLARE @i int = 1, @d date, @sql nvarchar(400);

    WHILE @i <= @MonthsAhead
    BEGIN
        SET @d = DATEADD(MONTH, @i, @first);

        IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values rv
                       JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                       WHERE pf.name = N'pf_AuditByMonth'
                         AND CAST(rv.value AS datetime2(3)) = CAST(@d AS datetime2(3)))
        BEGIN
            ALTER PARTITION SCHEME ps_AuditByMonth NEXT USED [AUDIT];
            SET @sql = N'ALTER PARTITION FUNCTION pf_AuditByMonth() SPLIT RANGE ('''
                     + CONVERT(nvarchar(10), @d, 120) + N''');';
            EXEC sp_executesql @sql;
            SET @Added = @Added + 1;
        END

        SET @i = @i + 1;
    END
END;
GO

-- D-247 / D-236 / НФ-8.4b: архівація аудиту. Партиції aud.* (CellChange,
-- StructureChange, SecurityEvent, PublicationEvent), ПОВНІСТЮ старші за
-- @OlderThanMonths місяців, перемикаються (SWITCH PARTITION) у дзеркала
-- arc.Audit* (12-archive-tables.sql) тієї ж структури на тій самій
-- ps_AuditByMonth. Рядки не копіюються і не видаляються: це метаданкова
-- операція, тому тригери незмінності (THROW 50060 на UPDATE/DELETE) НЕ
-- спрацьовують і НЕ вимикаються — аудит лишається незмінним, лише змінює
-- таблицю-власника (в архівних дзеркалах діють такі самі тригери).
--
-- Слід: кожен перенос = рядок itg.MaintenanceRun (JobCode = 'audit-archive',
-- Status = 'Succeeded', DetailsJson: таблиця, партиція, період (початок місяця),
-- кількість рядків, виконавець ORIGINAL_LOGIN(), поріг). Перенос і запис
-- журналу — в ОДНІЙ транзакції. Збій: відкат + рядок 'Failed' з текстом помилки
-- + повторний THROW. Ціль непорожня (у архіві вже є рядки цього місяця) —
-- партиція НЕ переноситься, рядок 'Degraded' (потрібна ручна розв'язка, runbook).
--
-- ⚠ Ідемпотентна: порожні/уже перенесені партиції пропускаються мовчки (без
-- рядка журналу). Читачі (AuditReader) дивляться лише в aud.* — архівований
-- період із UI історії зникає.
--
-- ✎ PSS-27 (аудит 20.11; рішення людини 20.11 №7 — мінімальна глибина аудиту 120
-- місяців): механіка переносу — у ВНУТРІШНІЙ `arc.usp_ArchiveAuditBefore` (межа
-- @Cutoff явно). Обліковий запис служби права EXECUTE на неї НЕ має (його немає в
-- переліку прав служби, runbook) — її кличе лише `arc.usp_ArchiveAudit` нижче (від
-- імені власника) і DBA вручну. Публічна `arc.usp_ArchiveAudit` сама рахує межу:
-- @OlderThanMonths НЕ менше 120 (менше — піднімається до 120), @Today не пізніше
-- завтрашнього дня сервера (THROW 50018). Раніше викликач з EXECUTE міг передати
-- (1, '2099-01-01') і перенести з `aud.*` (і з UI історії) весь аудит.
CREATE OR ALTER PROCEDURE arc.usp_ArchiveAuditBefore
    @Cutoff datetime2(3),
    @OlderThanMonths int,
    @PartitionsSwitched int = NULL OUTPUT,
    @RowsSwitched bigint = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET LOCK_TIMEOUT 30000;
    SET @PartitionsSwitched = 0;
    SET @RowsSwitched = 0;

    IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_AuditByMonth')
        RETURN;

    IF @Cutoff IS NULL
        THROW 50018, N'arc.usp_ArchiveAuditBefore: @Cutoff обов''язковий.', 1;

    DECLARE @maxPart int = $PARTITION.pf_AuditByMonth(@Cutoff) - 1;

    DECLARE @work TABLE
    (
        Ord int IDENTITY(1,1) PRIMARY KEY,
        Src nvarchar(128) NOT NULL,
        Dst nvarchar(128) NOT NULL,
        P   int           NOT NULL
    );

    INSERT INTO @work (Src, Dst, P)
    SELECT m.Src, m.Dst, p.partition_number
    FROM (VALUES (N'aud.CellChange',      N'arc.AuditCellChange'),
                 (N'aud.StructureChange', N'arc.AuditStructureChange'),
                 (N'aud.SecurityEvent',   N'arc.AuditSecurityEvent'),
                 (N'aud.PublicationEvent', N'arc.AuditPublicationEvent')) AS m (Src, Dst)
    JOIN sys.partitions AS p
      ON p.object_id = OBJECT_ID(m.Src) AND p.index_id = 1
     AND p.partition_number <= @maxPart AND p.rows > 0
    WHERE OBJECT_ID(m.Dst) IS NOT NULL
    ORDER BY p.partition_number, m.Src;

    DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @work);
    DECLARE @src nvarchar(128), @dst nvarchar(128), @p int, @sql nvarchar(1000);
    DECLARE @cnt bigint, @dstRows bigint, @lo datetime2(3), @started datetime2(3), @details nvarchar(2000);

    WHILE @i <= @n
    BEGIN
        SELECT @src = Src, @dst = Dst, @p = P FROM @work WHERE Ord = @i;
        SET @i = @i + 1;
        SET @started = SYSUTCDATETIME();

        SELECT @lo = CAST(rv.value AS datetime2(3))
        FROM sys.partition_range_values rv
        JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
        WHERE pf.name = N'pf_AuditByMonth' AND rv.boundary_id = @p - 1;
        IF @p = 1 SET @lo = NULL;

        SELECT @dstRows = ISNULL(SUM(rows), 0) FROM sys.partitions
        WHERE object_id = OBJECT_ID(@dst) AND index_id = 1 AND partition_number = @p;

        SET @sql = N'SELECT @c = COUNT_BIG(*) FROM ' + @src
                 + N' WHERE $PARTITION.pf_AuditByMonth(ChangedAt) = ' + CAST(@p AS nvarchar(10)) + N';';

        BEGIN TRY
            BEGIN TRAN;

            EXEC sp_executesql @sql, N'@c bigint OUTPUT', @c = @cnt OUTPUT;

            IF @cnt = 0
            BEGIN
                COMMIT;
                CONTINUE;
            END

            SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                + N',"periodStart":' + ISNULL(N'"' + CONVERT(nvarchar(10), @lo, 120) + N'"', N'null')
                + N',"rows":' + CAST(@cnt AS nvarchar(20))
                + N',"by":"' + STRING_ESCAPE(ORIGINAL_LOGIN(), N'json')
                + N'","olderThanMonths":' + ISNULL(CAST(@OlderThanMonths AS nvarchar(10)), N'null')
                + N',"cutoff":"' + CONVERT(nvarchar(10), @Cutoff, 120) + N'"}';

            IF @dstRows > 0
            BEGIN
                SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                    + N',"periodStart":' + ISNULL(N'"' + CONVERT(nvarchar(10), @lo, 120) + N'"', N'null')
                    + N',"reason":"ціль ' + @dst + N' має рядки в цій партиції; SWITCH пропущено"}';
                INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
                VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Degraded', @details);
                COMMIT;
                CONTINUE;
            END

            SET @sql = N'ALTER TABLE ' + @src + N' SWITCH PARTITION ' + CAST(@p AS nvarchar(10))
                     + N' TO ' + @dst + N' PARTITION ' + CAST(@p AS nvarchar(10)) + N';';
            EXEC sp_executesql @sql;

            INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
            VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Succeeded', @details);

            COMMIT;

            SET @PartitionsSwitched = @PartitionsSwitched + 1;
            SET @RowsSwitched = @RowsSwitched + @cnt;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK;

            SET @details = N'{"table":"' + @src + N'","partition":' + CAST(@p AS nvarchar(10))
                + N',"error":"' + STRING_ESCAPE(LEFT(ERROR_MESSAGE(), 500), N'json') + N'"}';
            INSERT INTO itg.MaintenanceRun (JobCode, StartedAt, FinishedAt, Status, DetailsJson)
            VALUES (N'audit-archive', @started, SYSUTCDATETIME(), N'Failed', @details);

            THROW;
        END CATCH
    END
END;
GO

-- Публічна точка архівації аудиту (нічний `PartitionCheckJob`, DBA). PSS-27: межу рахує САМА,
-- з обмеженнями, яких викликач не обійде:
--  * @OlderThanMonths < 120 (або NULL) → 120: мінімальна глибина аудиту в `aud.*` — 120 місяців
--    (рішення людини 20.11 №7). Застосунок передає налаштування `Retention.AuditMonths`
--    (`sys_ecr.SystemSetting`, з тією самою підлогою), але підлога тут — остання лінія;
--  * @Today пізніше за завтрашній день сервера (UTC) → THROW 50018: «сьогодні» з майбутнього
--    зсунуло б межу вперед. День запасу — розбіжність годинників вузла застосунку і SQL Server.
-- ⚠ WITH EXECUTE AS OWNER: обліковому запису застосунку DDL-прав не треба (D-66) — лише EXECUTE
-- на ЦЮ процедуру; `arc.usp_ArchiveAuditBefore` і ALTER TABLE … SWITCH виконуються від dbo.
CREATE OR ALTER PROCEDURE arc.usp_ArchiveAudit
    @OlderThanMonths int = 120,
    @Today date = NULL,
    @PartitionsSwitched int = NULL OUTPUT,
    @RowsSwitched bigint = NULL OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;
    SET @PartitionsSwitched = 0;
    SET @RowsSwitched = 0;

    DECLARE @serverToday date = CAST(SYSUTCDATETIME() AS date);
    IF @Today > DATEADD(DAY, 1, @serverToday)
    BEGIN
        DECLARE @future nvarchar(400) = N'arc.usp_ArchiveAudit: @Today ' + CONVERT(nvarchar(10), @Today, 120)
            + N' пізніше за дату сервера ' + CONVERT(nvarchar(10), @serverToday, 120)
            + N' (UTC); межа архівації рахується лише від поточної дати.';
        THROW 50018, @future, 1;
    END

    IF @OlderThanMonths IS NULL OR @OlderThanMonths < 120
        SET @OlderThanMonths = 120;

    DECLARE @t date = COALESCE(@Today, @serverToday);
    DECLARE @cutoff datetime2(3) =
        DATEADD(MONTH, -@OlderThanMonths, DATEFROMPARTS(YEAR(@t), MONTH(@t), 1));

    EXEC arc.usp_ArchiveAuditBefore
        @Cutoff = @cutoff,
        @OlderThanMonths = @OlderThanMonths,
        @PartitionsSwitched = @PartitionsSwitched OUTPUT,
        @RowsSwitched = @RowsSwitched OUTPUT;
END;
GO
