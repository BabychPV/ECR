-- ⚠ SET-опції задаються ЯВНО і першими: `sqlcmd` за замовчуванням має
-- `QUOTED_IDENTIFIER OFF`, а `SqlClient` — `ON`.
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

-- src/Ecr.Infrastructure/Persistence/Sql/14-agent-jobs.sql
--
-- Регламент обслуговування завданнями SQL Server Agent (`D-66`).
--
-- ⛔ ЧОМУ НЕ ЗАСТОСУНОК. У застосунку немає ані DDL-прав, ані права запису в
-- `arc.*` — і це не обмеження, а рішення: процес, який приймає HTTP-запити,
-- не повинен уміти рухати партиції й видаляти роки. Тому обслуговування
-- виконує Agent під **окремим principal**, і застосунок про нього не знає.
--
-- ⚠ Цього скрипта не існувало, поки локально стояв SQL Server Express: у
-- ньому Agent відсутній ЯК КОМПОНЕНТ, тому регламент не було де перевірити
-- (`P-04`). Після переходу на Developer Edition перевірити можна, і скрипт
-- з'явився.
--
-- ⛔ Скрипт НЕ створює логін і не роздає прав: хто саме виконує обслуговування
-- — рішення DBA замовника, і вигадувати ім'я принципала за нього не можна.
-- Завдання створюються від власника, під яким виконується цей скрипт;
-- змінити власника — один `sp_update_job @owner_login_name`.
--
-- ⚠ Ідемпотентний: повторний запуск оновлює кроки й розклади, а не створює
-- другий комплект.

IF DB_ID('msdb') IS NULL
    THROW 50040, N'msdb недоступна: SQL Server Agent не встановлено. На Express його немає взагалі.', 1;
GO

-- ── 1. Партиції на випередження ──────────────────────────────────────────
--
-- ⚠ Щомісяця, а не щодня: SPLIT порожньої партиції — операція метаданих, але
-- вона бере блокування схеми, і робити її щодня без потреби означає щодня
-- ризикувати вікном.
--
-- ⛔ Запас — шість місяців. Партиція, якої немає, не «створюється на льоту»:
-- запис за майбутній період потрапив би в останню наявну, а `$PARTITION`
-- повернув би для нього ту саму партицію, що й для попереднього року. Саме
-- через цю властивість архівація перевіряє межі окремо (`D-117`).
--
-- ⚠ Ім'я бази береться в КОЖНОМУ пакеті: змінна не переживає `GO`.
--
-- ⛔ Завдання спершу ВИДАЛЯЄТЬСЯ, потім створюється. `sp_add_jobstep` на
-- наявному кроці падає («step_name already exists»), а `sp_add_schedule`
-- мовчки створює ДРУГИЙ розклад із тим самим ім'ям — імена розкладів не
-- унікальні. Обидва роблять повторний запуск або помилкою, або тихим
-- дублюванням; видалити й створити наново — єдиний спосіб бути ідемпотентним.
--
-- ⚠ Розклад job-local (`sp_add_jobschedule`), а не спільний: спільний пережив
-- би видалення завдання і залишився б висіти нічий.
DECLARE @db sysname = DB_NAME();

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ECR: Partitions ahead')
    EXEC msdb.dbo.sp_delete_job @job_name = N'ECR: Partitions ahead', @delete_unused_schedule = 1;

EXEC msdb.dbo.sp_add_job
    @job_name = N'ECR: Partitions ahead',
    @description = N'arc.usp_EnsurePartitions: межі партицій на 6 місяців уперед (D-66).',
    @enabled = 1;

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'ECR: Partitions ahead',
    @step_name = N'EnsurePartitions',
    @subsystem = N'TSQL',
    @database_name = @db,
    @command = N'EXEC arc.usp_EnsurePartitions @MonthsAhead = 6;',
    @on_success_action = 1,
    @on_fail_action = 2,
    @retry_attempts = 0;

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'ECR: Partitions ahead',
    @name = N'ECR: monthly 02:40',
    @freq_type = 16,                  -- щомісяця
    @freq_interval = 1,               -- першого числа
    @freq_recurrence_factor = 1,
    @active_start_time = 24000;       -- 02:40

EXEC msdb.dbo.sp_add_jobserver @job_name = N'ECR: Partitions ahead';
GO

-- ── 2. Фізичні інваріанти схеми ──────────────────────────────────────────
--
-- ⚠ Це НЕ дублювання нічної задачі застосунку. Та перевіряє предметні
-- інваріанти (осиротілі рядки, розбіжності підсумків); ця — фізичні: чи всі
-- індекси партиційованих таблиць лежать на своїй схемі і чи не лишився
-- недовірений зовнішній ключ. Перше знає застосунок, друге — лише база.
--
-- ⛔ Недовірений ключ тут не дрібниця: саме в такому стані опинялася база
-- після масового завантаження, і оптимізатор ігнорував ключ на найгарячішій
-- таблиці системи, не сказавши жодного слова.
DECLARE @db sysname = DB_NAME();
DECLARE @physical nvarchar(max) = N'
IF EXISTS (SELECT 1 FROM sys.foreign_keys fk
            JOIN sys.tables t ON t.object_id = fk.parent_object_id
           WHERE fk.is_not_trusted = 1 AND fk.is_disabled = 0
             AND SCHEMA_NAME(t.schema_id) IN (N''doc'', N''calc'', N''aud''))
    THROW 50041, N''Є недовірені зовнішні ключі: оптимізатор їх ігнорує.'', 1;

IF EXISTS (SELECT 1 FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
           WHERE i.type IN (1, 2)
             AND SCHEMA_NAME(t.schema_id) = N''doc''
             AND t.name IN (N''TableInstance'', N''TableRow'', N''CellValue'')
             AND ds.type_desc <> N''PARTITION_SCHEME'')
    THROW 50042, N''Індекс партиційованої таблиці лежить поза схемою партиціонування.'', 1;';

IF EXISTS (SELECT 1 FROM msdb.dbo.sysjobs WHERE name = N'ECR: Physical checks')
    EXEC msdb.dbo.sp_delete_job @job_name = N'ECR: Physical checks', @delete_unused_schedule = 1;

EXEC msdb.dbo.sp_add_job
    @job_name = N'ECR: Physical checks',
    @description = N'Фізичні інваріанти схеми: довіреність FK, розміщення індексів (D-66).',
    @enabled = 1;

EXEC msdb.dbo.sp_add_jobstep
    @job_name = N'ECR: Physical checks',
    @step_name = N'PhysicalChecks',
    @subsystem = N'TSQL',
    @database_name = @db,
    @command = @physical,
    @on_success_action = 1,
    @on_fail_action = 2,
    @retry_attempts = 0;

EXEC msdb.dbo.sp_add_jobschedule
    @job_name = N'ECR: Physical checks',
    @name = N'ECR: nightly 03:10',
    @freq_type = 4,                   -- щодня
    @freq_interval = 1,
    @active_start_time = 31000;       -- 03:10

EXEC msdb.dbo.sp_add_jobserver @job_name = N'ECR: Physical checks';
GO

-- ⛔ Архівація року завданням НЕ ставиться — свідомо. Вона змінює фізичне
-- розміщення даних і незворотна в межах вікна; задача, що робить це «за
-- розкладом», рано чи пізно заархівує рік, який ще правлять. Її запускає
-- людина: `EXEC arc.usp_ArchiveYear @ProjectId, @FromPeriodKey, @ToPeriodKey`.
PRINT N'Завдання SQL Agent створені. Власник — той, під ким виконано скрипт; змінити: sp_update_job @owner_login_name.';
GO
