-- src/Ecr.Infrastructure/Persistence/Sql/16-resource-governor.sql
-- PSS-03. НЕОБОВ'ЯЗКОВИЙ скрипт ДЛЯ DBA: ізоляція фонового навантаження ECR (воркер перерахунку) від
-- інтерактивного (Api) на рівні SQL Server через Resource Governor.
--
-- ⛔ НЕ входить до `$scripts` у `deploy-ecr.ps1` / `setup-dev-db.ps1`: застосунок і розгортання його НЕ виконують
--    (обліковий запис застосунку не має DDL на рівні сервера, D-66). Запускає DBA вручну, коли вирішить
--    (рекомендації та рішення DBA: `docs/build/11-install-guide.md`, розділ «2.10 Налаштування SQL Server»).
--
-- Що робить (усе — в `master`, на рівні ІНСТАНСА, а не бази ECR):
--   1. Робоча група `ecr_background` у пулі `default`: MAX_DOP 2, REQUEST_MAX_MEMORY_GRANT_PERCENT 10,
--      GROUP_MAX_REQUESTS 8 (параметри нижче).
--   2. Функція-класифікатор `dbo.ecr_rg_classifier` у `master`: сеанси з `APP_NAME()` = `Ecr.Worker*` -> `ecr_background`,
--      решта (зокрема `Ecr.Api` та інші застосунки на інстансі) -> `default`. Імена задає сам застосунок
--      (`Application Name` у рядку підключення, PSS-03); значення, яке адміністратор задав у рядку, сильніше.
--   3. `ALTER RESOURCE GOVERNOR RECONFIGURE`.
--
-- ⚠ Якщо на інстансі УЖЕ є інший класифікатор (чужий) — скрипт нічого не змінює й зупиняється: об'єднати логіку
--    має DBA (класифікатор один на інстанс). Повторний запуск власного скрипта безпечний (ідемпотентний).
-- ⚠ Редакція: Enterprise, а на SQL Server 2025 (17.x) і новіших — заявлено й для Standard [НЕ ПЕРЕВІРЕНО на цільовій
--    збірці — підтвердити]. На іншій редакції скрипт зупиняється.
-- ⚠ Нова група впливає лише на НОВІ сеанси (існуючі лишаються у старій групі до перепідключення; пул з'єднань
--    застосунку перепідключається при перезапуску служб).
-- ⚠ Відкат: поставити `@Rollback = 1` нижче й запустити знову (або вручну: ALTER RESOURCE GOVERNOR WITH
--    (CLASSIFIER_FUNCTION = NULL); ALTER RESOURCE GOVERNOR RECONFIGURE; DROP WORKLOAD GROUP ecr_background;
--    DROP FUNCTION dbo.ecr_rg_classifier).
--
-- Запуск (UTF-8 без BOM, тому `-f i:65001`; обліковий запис — sysadmin / CONTROL SERVER):
--   sqlcmd -S <сервер> -d master -E -b -f i:65001 -i 16-resource-governor.sql
-- Перевірка:
--   SELECT g.name, g.max_dop, g.request_max_memory_grant_percent, g.group_max_requests
--     FROM sys.resource_governor_workload_groups g WHERE g.name = N'ecr_background';
--   SELECT s.program_name, s.group_id, COUNT(*) FROM sys.dm_exec_sessions s WHERE s.is_user_process = 1
--     GROUP BY s.program_name, s.group_id;

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

USE master;

-- ── Параметри (змінюються тут до запуску) ───────────────────────────────────────────────────────────────────
DECLARE @MaxDop int = 2;                    -- паралелізм запитів воркера
DECLARE @MemoryGrantPercent int = 10;       -- макс. частка пам'яті пулу на ОДИН запит воркера (дефолт SQL Server: 25)
DECLARE @GroupMaxRequests int = 8;          -- одночасних запитів воркера; решта чекає в черзі (0 = без межі)
DECLARE @AppNamePattern nvarchar(100) = N'Ecr.Worker%';   -- Ecr.Worker.Child (Infrastructure/DependencyInjection.cs)
DECLARE @Rollback bit = 0;                  -- 1 = прибрати групу й класифікатор

-- ── Редакція ────────────────────────────────────────────────────────────────────────────────────────────────
IF NOT (CAST(SERVERPROPERTY('EngineEdition') AS int) = 3 OR TRY_CAST(SERVERPROPERTY('ProductMajorVersion') AS int) >= 17)
BEGIN
    THROW 50016, N'Resource Governor недоступний у цій редакції SQL Server (потрібен Enterprise або SQL Server 2025+). Нічого не змінено.', 1;
END

DECLARE @ourFunctionId int = OBJECT_ID(N'master.dbo.ecr_rg_classifier');
DECLARE @currentClassifierId int = (SELECT classifier_function_id FROM sys.resource_governor_configuration);

IF ISNULL(@currentClassifierId, 0) <> 0 AND ISNULL(@ourFunctionId, 0) <> @currentClassifierId
BEGIN
    THROW 50017, N'На інстансі вже задано ІНШИЙ класифікатор Resource Governor. Нічого не змінено: DBA об''єднує логіку вручну (додати гілку APP_NAME() LIKE ''Ecr.Worker%'' -> ecr_background).', 1;
END

IF @Rollback = 1
BEGIN
    IF @ourFunctionId IS NOT NULL AND @currentClassifierId = @ourFunctionId
    BEGIN
        ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = NULL);
        ALTER RESOURCE GOVERNOR RECONFIGURE;
    END

    IF @ourFunctionId IS NOT NULL
        DROP FUNCTION dbo.ecr_rg_classifier;

    IF EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = N'ecr_background')
    BEGIN
        DROP WORKLOAD GROUP ecr_background;
        ALTER RESOURCE GOVERNOR RECONFIGURE;
    END

    PRINT N'Resource Governor: групу ecr_background і класифікатор ecr_rg_classifier прибрано.';
    RETURN;
END

-- ── Робоча група (CREATE або ALTER — ідемпотентно) ──────────────────────────────────────────────────────────
DECLARE @groupSql nvarchar(max) =
    CASE WHEN EXISTS (SELECT 1 FROM sys.resource_governor_workload_groups WHERE name = N'ecr_background')
         THEN N'ALTER WORKLOAD GROUP ecr_background WITH ('
         ELSE N'CREATE WORKLOAD GROUP ecr_background WITH (' END
    + N'MAX_DOP = ' + CAST(@MaxDop AS nvarchar(10))
    + N', REQUEST_MAX_MEMORY_GRANT_PERCENT = ' + CAST(@MemoryGrantPercent AS nvarchar(10))
    + N', GROUP_MAX_REQUESTS = ' + CAST(@GroupMaxRequests AS nvarchar(10))
    + N') USING [default];';
EXEC (@groupSql);

-- ── Класифікатор: ПЕРЕСТВОРЮЄТЬСЯ (а не пропускається), щоб зміна @AppNamePattern діяла при повторі ───────────
IF @ourFunctionId IS NOT NULL
BEGIN
    ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = NULL);
    ALTER RESOURCE GOVERNOR RECONFIGURE;
    DROP FUNCTION dbo.ecr_rg_classifier;
END

DECLARE @functionSql nvarchar(max) = N'
CREATE FUNCTION dbo.ecr_rg_classifier()
RETURNS sysname
WITH SCHEMABINDING
AS
BEGIN
    -- ⚠ Мінімум логіки: функція виконується при КОЖНОМУ вході. Без звернень до таблиць.
    DECLARE @group sysname = N''default'';
    IF APP_NAME() LIKE N''' + REPLACE(@AppNamePattern, N'''', N'''''') + N'''
        SET @group = N''ecr_background'';
    RETURN @group;
END';
EXEC (@functionSql);

ALTER RESOURCE GOVERNOR WITH (CLASSIFIER_FUNCTION = dbo.ecr_rg_classifier);
ALTER RESOURCE GOVERNOR RECONFIGURE;

PRINT N'Resource Governor: група ecr_background і класифікатор ecr_rg_classifier увімкнено.';
