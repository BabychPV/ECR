-- tools/e2e/pi-raw-data-seed.sql
-- D-463: зібрані точки PI для стенда Playwright (`tools/e2e-stand.ps1`, крок «дані з PI»).
--
-- ⛔ Лише ТИМЧАСОВА база стенда: продуктовий сід і шаблони не змінюються. Шлях запису збору
-- (`CollectionStore`) тут не потрібен — читаюча сторона D-463 бачить рівно рядки `ext.RawDataPoint`,
-- а живого PI на стенді немає.
-- ⚠ Сутність ВИМКНЕНА: активна сутність без завершеного збору — прогалина для `SourcesHealthCheck`,
-- і `/admin/health` на стенді став би «Degraded» для чужих сценаріїв. Мапінг на комірку сценарій
-- заводить сам через API і сам прибирає (`piRawDataLive.spec.ts`), щоб не зачепити перенос версій.
--
-- Змінні sqlcmd: $(DocumentId) — документ стенда, $(PeriodKey) — відкритий період.
-- Вихід: останній рядок — Id сутності.
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

DECLARE @document bigint = $(DocumentId);
DECLARE @period int = $(PeriodKey);
DECLARE @path nvarchar(400) = N'\\E2E\Plant\Stack01|CO';
DECLARE @now datetime2(3) = SYSUTCDATETIME();

DECLARE @start datetime2(3), @end datetime2(3);
SELECT @start = CAST(p.PeriodStart AS datetime2(3)), @end = DATEADD(DAY, 1, CAST(p.PeriodEnd AS datetime2(3)))
FROM doc.Document AS d
JOIN doc.Period AS p ON p.ProjectId = d.ProjectId AND p.PeriodKey = @period
WHERE d.Id = @document;

IF @start IS NULL THROW 50463, 'pi-raw-data-seed: period of the stand document not found', 1;

BEGIN TRANSACTION;

INSERT INTO ext.DataSource (Code, NameL10n, Transport, Endpoint, SecretName, IsActive, MaxParallel)
VALUES (N'E2E_PI', N'{"en":"E2E PI (stand)"}', 0, N'https://pi.e2e.test/piwebapi', N'e2e-pi-none', 0, 1);
DECLARE @source int = SCOPE_IDENTITY();

INSERT INTO ext.SourceEntity (DataSourceId, Code, DisplayName, EntityPath, IsActive, SourceKind, OnMissingInSource, ValidToInclusive)
VALUES (@source, N'E2E_STACK_01', N'E2E Stack 01', @path, 0, 0, 0, 0);
DECLARE @entity int = SCOPE_IDENTITY();

INSERT INTO itg.CollectionRun (SourceEntityId, RangeFrom, RangeTo, StartedAt, FinishedAt, PointsRetrieved, Status, IsCatchUp)
VALUES (@entity, DATEADD(YEAR, -1, @start), @end, @now, @now, 0, N'Succeeded', 0);
DECLARE @run bigint = SCOPE_IDENTITY();

-- Період документа: щогодини, значення — пила 10..33 з одним текстовим рядком і «сумнівною» якістю.
INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality, RetrievedAt, CollectionRunId)
SELECT @entity, @path, DATEADD(HOUR, t.n, @start),
       CASE WHEN t.n = 5 THEN NULL ELSE 10 + (t.n % 24) END,
       CASE WHEN t.n = 5 THEN N'=SUM(A1)' END,
       NULL,
       CASE WHEN t.n % 50 = 0 THEN N'Questionable' ELSE N'Good' END,
       @now, @run
FROM (SELECT TOP (DATEDIFF(HOUR, @start, @end)) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS n
      FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS t;

-- Рік до періоду: щодня — для пресета «рік».
INSERT INTO ext.RawDataPoint (SourceEntityId, SourcePath, [Timestamp], ValueNumeric, ValueString, UnitId, Quality, RetrievedAt, CollectionRunId)
SELECT @entity, @path, DATEADD(DAY, -t.n - 1, @start), 100 + t.n, NULL, NULL, N'Good', @now, @run
FROM (SELECT TOP (365) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) - 1 AS n FROM sys.all_objects) AS t;

-- Покриття: успішний інтервал з прогалиною в 1 добу посеред періоду.
INSERT INTO itg.CollectionCoverage (SourceEntityId, CoveredFrom, CoveredTo, CollectionRunId)
VALUES (@entity, DATEADD(YEAR, -1, @start), DATEADD(DAY, 10, @start), @run),
       (@entity, DATEADD(DAY, 11, @start), @end, @run);

COMMIT TRANSACTION;

SELECT @entity;
