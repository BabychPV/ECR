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

-- src/Ecr.Infrastructure/Persistence/Sql/03-archive-proc.sql
-- Виконується SQL Agent під окремим principal: у застосунку немає ані DDL-прав,
-- ані права запису в arc.* (D-66, B01 §6.4).
-- РУЧНИЙ ІНСТРУМЕНТ DBA. Штатний шлях його НЕ ПОТРЕБУЄ (`D-117`).
--
-- ⛔ Зі штатного шляху процедуру прибрано. Зняття і повернення ключів тепер
-- відбувається в ОДНІЙ транзакції всередині `usp_ArchiveYear`, а DDL і
-- `TRUNCATE` у SQL Server транзакційні: стану «`DROP` зафіксовано, `ADD` — ні»
-- не буває. Ризик «схема без обмежень після збою», який я називав раніше,
-- знімає не `CATCH`, а сама транзакція.
--
-- ⚠ Викликати вручну — лише якщо ключів немає з причини, якої транзакція не
-- допускає: наприклад, їх зняли окремою командою під час розслідування.
--
-- ⚠ WITH CHECK, а не NOCHECK: недовірене обмеження оптимізатор ігнорує, і
-- «ключ є» перетворилося б на «ключ намальовано».
CREATE OR ALTER PROCEDURE arc.usp_RestoreArchiveConstraints
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_TableRow_Instance')
        ALTER TABLE doc.TableRow WITH CHECK
            ADD CONSTRAINT FK_TableRow_Instance FOREIGN KEY (PeriodKey, TableInstanceId)
            REFERENCES doc.TableInstance (PeriodKey, Id);

    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = N'FK_CellValue_Row')
        ALTER TABLE doc.CellValue WITH CHECK
            ADD CONSTRAINT FK_CellValue_Row FOREIGN KEY (PeriodKey, TableRowId)
            REFERENCES doc.TableRow (PeriodKey, Id);
END;
GO

CREATE OR ALTER PROCEDURE arc.usp_ArchiveYear
    @ProjectId     int,
    @FromPeriodKey int,
    @ToPeriodKey   int,
    @BatchSize     int = 500000
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- ⛔ Масштаб сум — 16, як у `doc.CellValue.ValueNumeric` (`D-148`). З 10
    -- знаками звірка не падала б, а ТИХО СЛАБШАЛА: обидві суми однаково
    -- округлилися б, збіг лишився б, і знаки 11–16 перестали б доводити
    -- будь-що.
    --
    -- ⚠ Precision лишається 38 і після переходу стовпця на `decimal(34,16)`,
    -- і ширше вже НЕ БУВАЄ: 38 — максимум SQL Server, і рівно його дає
    -- `SUM(decimal(34,16))` як власний тип результату (перевірено запитом), тож
    -- оголошення точно збігається з тим, що рахується. Формальний запас над
    -- стовпцем справді впав із 10¹⁰ до 10⁴ рядків (22 цілі розряди суми проти
    -- 18 у стовпця), але ДОСЯЖНИЙ запас не змінився: `System.Decimal` несе лише
    -- 29 значущих цифр, тож при масштабі 16 застосунок фізично не може записати
    -- більше за ~7.9·10¹² (13 цілих розрядів) — а це 10⁹ рядків запасу.
    DECLARE @k int, @srcCount bigint, @srcSum decimal(38,16);
    DECLARE @dstCount bigint, @dstSum decimal(38,16);
    DECLARE @p1 int, @p12 int, @range nvarchar(40), @sql nvarchar(600);
    DECLARE @periods int = @ToPeriodKey - @FromPeriodKey + 1;

    ------------------------------------------------------------------------
    -- ЗАПОБІЖНИК 1. Партиція — ПО ПЕРІОДУ, а не по проєкту.
    --
    -- ⛔ `pf_ByPeriodKey` не знає про проєкти: `TRUNCATE ... WITH (PARTITIONS)`
    --    звільняє період ЦІЛКОМ, разом із даними всіх проєктів. Тому
    --    архівувати «рік одного проєкту», поки інший проєкт працює в тих
    --    самих періодах, означає знищити його живі дані.
    --
    -- ⚠ @ProjectId — це проєкт, ЗАРАДИ якого прогін запустили; ФІЗИЧНО
    --    процедура архівує період для всіх. Ця перевірка не дає зачепити
    --    ЖИВИЙ проєкт, а тих, кого зачеплено законно, записує журнал (нижче).
    --    Раніше журнал знав лише про @ProjectId — і різниця між «для одного»
    --    і «для всіх» виявлялася після того, як дані зникли.
    ------------------------------------------------------------------------
    IF EXISTS (
        SELECT 1
        FROM doc.Project AS p
        WHERE p.Status <> 4                                   -- не Archived
          AND EXISTS (SELECT 1 FROM doc.Period AS d
                       WHERE d.ProjectId = p.Id
                         AND d.PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey))
    BEGIN
        DECLARE @blockers nvarchar(400) = STUFF((
            SELECT N', ' + p.Code
            FROM doc.Project AS p
            WHERE p.Status <> 4
              AND EXISTS (SELECT 1 FROM doc.Period AS d
                           WHERE d.ProjectId = p.Id
                             AND d.PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
            FOR XML PATH(''), TYPE).value('.', 'nvarchar(400)'), 1, 2, N'');

        DECLARE @msg nvarchar(600) =
            N'Період ' + CAST(@FromPeriodKey AS nvarchar(10)) + N'..' +
            CAST(@ToPeriodKey AS nvarchar(10)) +
            N' використовують незаархівовані проєкти: ' + @blockers +
            N'. Звільнення партиції знищило б їхні дані.';

        THROW 50012, @msg, 1;
    END;

    ------------------------------------------------------------------------
    -- ЗАПОБІЖНИК 2. Межі партицій для цього діапазону мусять існувати.
    --
    -- ⛔ `$PARTITION` для ключа ПОНАД останню межу повертає останню партицію.
    --    Якщо межі на рік не створені, @p1 і @p12 зійдуться в одну — і
    --    `TRUNCATE` зачепив би чужі дані. Це не теорія: саме через цю
    --    властивість `D-108` обмежив Sequence до 12.
    ------------------------------------------------------------------------
    SET @p1  = $PARTITION.pf_ByPeriodKey(@FromPeriodKey);
    SET @p12 = $PARTITION.pf_ByPeriodKey(@ToPeriodKey);

    IF @p12 - @p1 <> @periods - 1
    BEGIN
        DECLARE @bounds nvarchar(400) =
            N'Межі партицій для ' + CAST(@FromPeriodKey AS nvarchar(10)) + N'..' +
            CAST(@ToPeriodKey AS nvarchar(10)) + N' не створені: очікувалося ' +
            CAST(@periods AS nvarchar(10)) + N' партицій, знайдено ' +
            CAST(@p12 - @p1 + 1 AS nvarchar(10)) + N'.';

        THROW 50013, @bounds, 1;
    END;

    ------------------------------------------------------------------------
    -- ЖУРНАЛ — на КОЖЕН зачеплений проєкт, а не лише на @ProjectId.
    --
    -- ⛔ Попередня версія записувала один рядок `itg.ArchiveRun` — на
    --    @ProjectId. Фізично ж `TRUNCATE ... WITH (PARTITIONS)` звільняє
    --    період ЦІЛКОМ (див. ЗАПОБІЖНИК 1), тож дані сусіда з тих самих
    --    періодів теж переїжджали в `arc.*`. А `ArchiveAwareCellReader`
    --    вирішує «читати з архіву чи з гарячої схеми» САМЕ за цим журналом:
    --    без свого рядка сусід вважався незаархівованим, читач ішов у
    --    звільнену гарячу партицію і повертав ПОРОЖНЬО. Без помилки, без
    --    попередження — рік просто зникав із звітів.
    --
    -- ⚠ ЗАПОБІЖНИК 1 не закривав цю дірку: він пропускає проєкти зі
    --    `Status = 4`, а «позначений заархівованим» і «фізично перенесений» —
    --    різні стани (позначку ставить людина, перенесення робить ця
    --    процедура після річного грейсу). Проєкт у проміжку між ними для
    --    запобіжника невидимий, а його дані — у тій самій партиції.
    --
    -- ⚠ Обрано саме журналювання, а не звуження `TRUNCATE` до одного
    --    проєкту: звузити його НЕМА ЧИМ. `pf_ByPeriodKey` партиціонує по
    --    `PeriodKey`, і `ProjectId` у `doc.CellValue`/`doc.TableRow` навіть
    --    не колонка — проєкт видно лише через `TableInstance → Document`.
    --    Єдина альтернатива — `DELETE` по проєкту, а це ~108 млн рядків
    --    через журнал транзакцій замість звільнення метаданих.
    ------------------------------------------------------------------------
    DECLARE @Runs TABLE (RunId bigint PRIMARY KEY, ProjectId int NOT NULL);

    INSERT INTO itg.ArchiveRun (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status)
    OUTPUT inserted.Id, inserted.ProjectId INTO @Runs (RunId, ProjectId)
    VALUES (@ProjectId, N'ToArchive', @FromPeriodKey, @ToPeriodKey, SYSUTCDATETIME(), N'Running');

    -- ⚠ `RowsMoved` у супутніх прогонах лишається нулем НАВМИСНО: рядки
    -- рахуються по партиції, а не по проєкту, і приписати сусідові чуже число
    -- означало б вигадати звітну величину. Що саме сталося — каже примітка.
    INSERT INTO itg.ArchiveRun
        (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status, ErrorMessage)
    OUTPUT inserted.Id, inserted.ProjectId INTO @Runs (RunId, ProjectId)
    SELECT DISTINCT d.ProjectId, N'ToArchive', @FromPeriodKey, @ToPeriodKey,
           SYSUTCDATETIME(), N'Running',
           N'Спільна партиція: період звільнено прогоном проєкту '
           + CAST(@ProjectId AS nvarchar(10)) + N'.'
    FROM doc.Period AS d
    WHERE d.PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey
      AND d.ProjectId <> @ProjectId;

    -- Позначка «архівується» — теж на всіх: дані сусіда зараз переїжджають
    -- так само, як і свої.
    UPDATE doc.Project SET IsArchiving = 1 WHERE Id IN (SELECT ProjectId FROM @Runs);

    BEGIN TRY

    ------------------------------------------------------------------------
    -- КРОК 1. Копія і звірка. ПОЗА транзакцією: довго і відновлювано.
    --
    -- ⚠ Ідемпотентність ЯВНА: цільові партиції arc.* спершу очищаються. Без
    --    цього повторний запуск після збою кроку 2 подвоїв би архів — і сума
    --    зійшлася б лише випадково.
    ------------------------------------------------------------------------
    SET @range = CAST(@p1 AS nvarchar(10)) + N' TO ' + CAST(@p12 AS nvarchar(10));

    ------------------------------------------------------------------------
    -- ⛔ «Вже заархівовано» — окремий випадок, і без нього ідемпотентність
    --    ЗНИЩУЄ архів. Якщо джерело порожнє, а в архіві рядки є, то рік уже
    --    перенесено: очищення цілі й копіювання з порожнього джерела
    --    залишило б порожній архів, а прогін звітував би про успіх.
    --
    -- ⚠ Знайдено тестом на повторний прогін — саме тим, який я написав, щоб
    --    перевірити ідемпотентність. Різниця між «повторити після збою» і
    --    «повторити після успіху» тут не косметична: у першому випадку
    --    джерело на місці, у другому його вже немає.
    ------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM doc.CellValue
                    WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
       AND EXISTS (SELECT 1 FROM arc.CellValue
                    WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
    BEGIN
        UPDATE itg.ArchiveRun
           SET Status = N'Completed', FinishedAt = SYSUTCDATETIME(),
               LastDonePeriodKey = @ToPeriodKey,
               ErrorMessage = N'Період уже заархівовано: джерело порожнє, архів на місці.'
         WHERE Id IN (SELECT RunId FROM @Runs);

        UPDATE doc.Project SET IsArchiving = 0 WHERE Id IN (SELECT ProjectId FROM @Runs);
        RETURN;
    END;

    -- ⚠ Тут саме DELETE, а не TRUNCATE WITH (PARTITIONS): `arc.*` лежить на
    -- окремій файловій групі колонстором і НЕ партиційована (`02a` §arc,
    -- `D-23`). Партиційний TRUNCATE на ній падає, а TRUNCATE цілої таблиці
    -- знищив би інші роки.
    --
    -- ⚠ Виконується лише коли є що прибирати: у звичайному прогоні це
    -- перевірка існування, а не сканування. Ціна платиться лише на повторі
    -- після збою — і саме там вона потрібна, бо без неї архів подвоївся б.
    -- ⚠ Перевіряються ВСІ три таблиці, а не лише `arc.CellValue`: невдалий
    -- прогін міг лягти між вставками і лишити рядки в `arc.TableRow` без
    -- жодної комірки. Перевірка по одній таблиці пропустила б їх, і повторний
    -- прогін подвоїв би саме те, чого не видно в сумах.
    IF EXISTS (SELECT 1 FROM arc.CellValue
                WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
       OR EXISTS (SELECT 1 FROM arc.TableRow
                   WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
       OR EXISTS (SELECT 1 FROM arc.TableInstance
                   WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey)
    BEGIN
        DELETE FROM arc.CellValue     WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;
        DELETE FROM arc.TableRow      WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;
        DELETE FROM arc.TableInstance WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;
    END;

    SET @k = @FromPeriodKey;

    WHILE @k <= @ToPeriodKey
    BEGIN
        SELECT @srcCount = COUNT_BIG(*),
               @srcSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM doc.CellValue WHERE PeriodKey = @k;

        -- TABLOCK → мінімальне логування і прямий запис у columnstore rowgroups.
        INSERT INTO arc.CellValue WITH (TABLOCK)
            (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
             ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty)
        SELECT PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty
        FROM doc.CellValue WHERE PeriodKey = @k;

        INSERT INTO arc.TableRow WITH (TABLOCK)
            (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt)
        SELECT PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, ModifiedAt
        FROM doc.TableRow WHERE PeriodKey = @k;

        INSERT INTO arc.TableInstance WITH (TABLOCK)
            (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
        SELECT PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt
        FROM doc.TableInstance WHERE PeriodKey = @k;

        SELECT @dstCount = COUNT_BIG(*),
               @dstSum   = ISNULL(SUM(ValueNumeric), 0)
        FROM arc.CellValue WHERE PeriodKey = @k;

        -- ⛔ Розбіжність → СТОП. ДАНІ З ДЖЕРЕЛА НЕ ВИДАЛЯЮТЬСЯ — це головне
        --    правило процедури, і крок 2 не починається взагалі.
        IF (@srcCount <> @dstCount OR @srcSum <> @dstSum)
        BEGIN
            INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 2, N'ARCHIVE_CHECKSUM', N'Period', @k,
                    N'Розбіжність контрольних сум при архівації; дані джерела збережено.');

            UPDATE itg.ArchiveRun
               SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), LastDonePeriodKey = @k - 1,
                   ErrorMessage = N'Checksum mismatch'
             WHERE Id IN (SELECT RunId FROM @Runs);

            UPDATE doc.Project SET IsArchiving = 0 WHERE Id IN (SELECT ProjectId FROM @Runs);
            THROW 50010, N'Розбіжність контрольних сум при архівації.', 1;
        END;

        UPDATE itg.ArchiveRun
           SET RowsMoved = RowsMoved
                         + CASE WHEN ProjectId = @ProjectId THEN @srcCount ELSE 0 END,
               LastDonePeriodKey = @k
         WHERE Id IN (SELECT RunId FROM @Runs);

        SET @k = @k + 1;
    END;

    ------------------------------------------------------------------------
    -- КРОК 2. Звільнення партицій. ОДНА транзакція, атомарна (`D-117`).
    --
    -- ⛔ DDL і TRUNCATE у SQL Server ТРАНЗАКЦІЙНІ. Не буває стану, у якому
    --    `DROP` зафіксовано, а `ADD` — ні: будь-який збій відкочує і ключі, і
    --    дані. Саме тому `usp_RestoreArchiveConstraints` зі штатного шляху
    --    прибрано — вона не має чого відновлювати.
    --
    -- ⚠ Поки транзакція триває, таблиці під Sch-M-блокуванням: писати в них
    --    фізично неможливо. «Вікно без обмежень» — це вікно, у якому немає що
    --    перевіряти.
    --
    -- ⚠ Порядок звільнення — від ДОЧІРНЬОЇ таблиці до батьківської: комірки,
    --    рядки, екземпляри.
    --
    -- ⚠ `WITH CHECK` сканує doc.CellValue цілком (~108 млн рядків) і тримає
    --    Sch-M: читачі, включно з SSRS, чекають. Вартість заміряти на DEV;
    --    за порогом 30 хв — двофазний варіант із `D-117`.
    ------------------------------------------------------------------------
    BEGIN TRAN;

        ALTER TABLE doc.CellValue DROP CONSTRAINT FK_CellValue_Row;
        ALTER TABLE doc.TableRow  DROP CONSTRAINT FK_TableRow_Instance;

        --------------------------------------------------------------------
        -- ЗАПОБІЖНИК 3. Повторна звірка В МОМЕНТ знищення.
        --
        -- ⛔ Суми з кроку 1 знімалися по одній партиції за раз і до кінця
        --    прогону встигали застаріти на години. Усе, що лягло в гарячу
        --    схему в цьому проміжку, `TRUNCATE` знищував — а прогін звітував
        --    `Completed`, бо звіряв стан, якого вже не існувало. Контрольна
        --    сума, знята задовго до видалення, не доводить нічого про те, що
        --    видаляють.
        --
        -- ⚠ Проміжок закритий не «уважнішим кроком 1», а МІСЦЕМ перевірки:
        --    вона стоїть ПІСЛЯ `ALTER TABLE ... DROP CONSTRAINT`, тобто коли
        --    транзакція вже тримає Sch-M на `doc.CellValue` і `doc.TableRow`.
        --    Під Sch-M писати в них фізично неможливо, тож між цим `SELECT` і
        --    `TRUNCATE` нового рядка з'явитися не може.
        --
        -- ⚠ `TABLOCKX` — не перестраховка. База працює під RCSI (`06`), де
        --    читач за замовчуванням бачить ЗНІМОК і не блокує писача; на
        --    `doc.TableInstance` Sch-M на цей момент ще немає, і без явного
        --    блокування перевірка звіряла б застарілий знімок.
        --
        -- ⚠ Звіряється з `arc.*`, а не з числами кроку 1: архів — це те, що
        --    ЗАЛИШИТЬСЯ, і питання стоїть саме так — «чи все, що зараз у
        --    джерелі, вже лежить в архіві».
        --------------------------------------------------------------------
        DECLARE @gapSrcCells bigint, @gapDstCells bigint;
        DECLARE @gapSrcSum decimal(38,16), @gapDstSum decimal(38,16);
        DECLARE @gapSrcRows bigint, @gapDstRows bigint;
        DECLARE @gapSrcInst bigint, @gapDstInst bigint;

        SELECT @gapSrcCells = COUNT_BIG(*), @gapSrcSum = ISNULL(SUM(ValueNumeric), 0)
        FROM doc.CellValue WITH (TABLOCKX)
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        SELECT @gapSrcRows = COUNT_BIG(*)
        FROM doc.TableRow WITH (TABLOCKX)
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        SELECT @gapSrcInst = COUNT_BIG(*)
        FROM doc.TableInstance WITH (TABLOCKX)
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        SELECT @gapDstCells = COUNT_BIG(*), @gapDstSum = ISNULL(SUM(ValueNumeric), 0)
        FROM arc.CellValue
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        SELECT @gapDstRows = COUNT_BIG(*)
        FROM arc.TableRow
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        SELECT @gapDstInst = COUNT_BIG(*)
        FROM arc.TableInstance
        WHERE PeriodKey BETWEEN @FromPeriodKey AND @ToPeriodKey;

        IF @gapSrcCells <> @gapDstCells OR @gapSrcSum <> @gapDstSum
           OR @gapSrcRows <> @gapDstRows OR @gapSrcInst <> @gapDstInst
        BEGIN
            -- ⛔ `THROW` усередині транзакції: `XACT_ABORT` відкочує і зняття
            --    ключів, і — головне — не дає дійти до `TRUNCATE`. Джерело
            --    лишається цілим, повторний прогін перенесе його разом із
            --    тим, що додалося (крок 1 ідемпотентний).
            DECLARE @gapMsg nvarchar(600) =
                N'Джерело змінилося після звірки сум: комірок '
                + CAST(@gapSrcCells AS nvarchar(20)) + N' проти '
                + CAST(@gapDstCells AS nvarchar(20)) + N' в архіві, рядків '
                + CAST(@gapSrcRows AS nvarchar(20)) + N' проти '
                + CAST(@gapDstRows AS nvarchar(20))
                + N'. Партиції не звільнено.';

            THROW 50014, @gapMsg, 1;
        END;

        SET @sql = N'TRUNCATE TABLE doc.CellValue     WITH (PARTITIONS (' + @range + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableRow      WITH (PARTITIONS (' + @range + N'));';
        EXEC sp_executesql @sql;
        SET @sql = N'TRUNCATE TABLE doc.TableInstance WITH (PARTITIONS (' + @range + N'));';
        EXEC sp_executesql @sql;

        ALTER TABLE doc.TableRow WITH CHECK
            ADD CONSTRAINT FK_TableRow_Instance FOREIGN KEY (PeriodKey, TableInstanceId)
            REFERENCES doc.TableInstance (PeriodKey, Id);

        ALTER TABLE doc.CellValue WITH CHECK
            ADD CONSTRAINT FK_CellValue_Row FOREIGN KEY (PeriodKey, TableRowId)
            REFERENCES doc.TableRow (PeriodKey, Id);

    COMMIT;

    END TRY
    BEGIN CATCH
        -- ⚠ Відкат робить XACT_ABORT; тут лишається тільки зняти позначку і
        -- підняти помилку далі. Ключі й дані повернула транзакція.
        IF @@TRANCOUNT > 0 ROLLBACK;

        -- ⚠ Запис у журнал знахідок — ПІСЛЯ відкату: усередині транзакції він
        -- відкотився б разом із нею, і найдорожчий випадок (дані змінилися
        -- під операцією) не лишив би жодного сліду.
        IF ERROR_NUMBER() = 50014
            INSERT INTO aud.ConsistencyIssue
                (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 2, N'ARCHIVE_GAP', N'Period', @FromPeriodKey,
                    N'Джерело змінилося між звіркою сум і звільненням партиції; '
                    + N'дані джерела збережено.');

        UPDATE itg.ArchiveRun
           SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), ErrorMessage = ERROR_MESSAGE()
         WHERE Id IN (SELECT RunId FROM @Runs);

        UPDATE doc.Project SET IsArchiving = 0 WHERE Id IN (SELECT ProjectId FROM @Runs);
        THROW;
    END CATCH;

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME()
     WHERE Id IN (SELECT RunId FROM @Runs);
    UPDATE doc.Project SET IsArchiving = 0 WHERE Id IN (SELECT ProjectId FROM @Runs);
END;
GO

-- Розархівація: зворотний напрям тієї самої процедури.
--
-- ⚠ Потрібна не «про всяк випадок»: перерахунок закритого року з окремим
-- погодженням (ФВ-9.7) читає дані звідти, де вони лежать, і рахувати по
-- columnstore-архіву з тими самими бюджетами неможливо. Тому рік повертають
-- у гарячу схему, рахують і архівують знову.
--
-- ⛔ Те саме головне правило: дані з АРХІВУ не видаляються, поки суми не
-- збіглися. Розархівація, що втратила рядок, гірша за архівацію, що його не
-- перенесла: там оригінал на місці, тут його вже немає.
CREATE OR ALTER PROCEDURE arc.usp_RestoreYear
    @ProjectId     int,
    @FromPeriodKey int,
    @ToPeriodKey   int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @RunId bigint, @k int, @srcCount bigint, @dstCount bigint;

    INSERT INTO itg.ArchiveRun (ProjectId, Direction, FromPeriodKey, ToPeriodKey, StartedAt, Status)
    VALUES (@ProjectId, N'FromArchive', @FromPeriodKey, @ToPeriodKey, SYSUTCDATETIME(), N'Running');
    SET @RunId = SCOPE_IDENTITY();

    UPDATE doc.Project SET IsArchiving = 1 WHERE Id = @ProjectId;

    SET @k = @FromPeriodKey;

    WHILE @k <= @ToPeriodKey
    BEGIN
        SELECT @srcCount = COUNT_BIG(*) FROM arc.CellValue WHERE PeriodKey = @k;

        -- Порядок зворотний до архівації: спершу батьківські рядки, потім
        -- комірки. Інакше FK не дає вставити комірку без свого рядка.
        INSERT INTO doc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
        SELECT PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt
        FROM arc.TableInstance WHERE PeriodKey = @k;

        INSERT INTO doc.TableRow (PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal,
                                  IsDeleted, IsOrphaned, ModifiedAt)
        SELECT PeriodKey, Id, TableInstanceId, RowKey, RowDefId, Ordinal, IsDeleted, 0, ModifiedAt
        FROM arc.TableRow WHERE PeriodKey = @k;

        INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString,
                                   ValueNumeric, ValueDate, ValueBool, ValueRegistryEntryId,
                                   ValueUnitId, IsCalculated, IsEmpty)
        SELECT PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueString, ValueNumeric,
               ValueDate, ValueBool, ValueRegistryEntryId, ValueUnitId, IsCalculated, IsEmpty
        FROM arc.CellValue WHERE PeriodKey = @k;

        SELECT @dstCount = COUNT_BIG(*) FROM doc.CellValue WHERE PeriodKey = @k;

        IF (@srcCount <> @dstCount)
        BEGIN
            INSERT INTO aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 3, N'RESTORE_CHECKSUM', N'Period', @k,
                    N'Розбіжність при розархівації; дані архіву збережено.');

            UPDATE itg.ArchiveRun
               SET Status = N'Failed', FinishedAt = SYSUTCDATETIME(), LastDonePeriodKey = @k - 1,
                   ErrorMessage = N'Restore count mismatch'
             WHERE Id = @RunId;

            UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
            THROW 50011, N'Розбіжність кількості рядків при розархівації.', 1;
        END

        UPDATE itg.ArchiveRun
           SET RowsMoved = RowsMoved + @srcCount, LastDonePeriodKey = @k
         WHERE Id = @RunId;

        SET @k = @k + 1;
    END

    UPDATE itg.ArchiveRun SET Status = N'Completed', FinishedAt = SYSUTCDATETIME() WHERE Id = @RunId;
    UPDATE doc.Project SET IsArchiving = 0 WHERE Id = @ProjectId;
END;
GO
