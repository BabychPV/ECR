-- tools/e2e/pi-events-seed.sql
-- D-465: події PI (Event Frames) для стенда Playwright (`tools/e2e-stand.ps1`, крок «події з PI»).
--
-- ⛔ Лише ТИМЧАСОВА база стенда: продуктовий сід і шаблони не змінюються. Живого PI на стенді немає, а «Дані з PI →
-- Події» читають рівно те, що синк уже записав: зв'язки (`ext.SourceEventLink`), значення атрибутів за останнім
-- прочитанням (`ext.SourceEventAttributeValue`) і журнал видалень (`aud.StructureChange`, `SourceEventRowRemoved`).
-- ⚠ Сутність ВИМКНЕНА, мапінг ПРИЗУПИНЕНИЙ: синк не запускається, `SourcesHealthCheck` чужих сценаріїв не змінюється,
-- а призупинений мапінг не блокує перенос версії документа (`MigrateDocumentVersionHandler`: лише активні).
-- ⚠ Події — за останні 20 діб від «зараз» (пресет «30 днів» сторінки), у межах вікна перевірки не потрібні.
--
-- Змінні sqlcmd: $(DocumentId) — документ стенда, $(PeriodKey) — відкритий період.
-- Вихід: останній рядок — Id сутності.
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

DECLARE @document bigint = $(DocumentId);
DECLARE @period int = $(PeriodKey);
DECLARE @now datetime2(3) = SYSUTCDATETIME();

DECLARE @instance bigint, @table int;
SELECT TOP (1) @instance = i.Id, @table = i.TableDefId
  FROM doc.TableInstance AS i
 WHERE i.DocumentId = @document AND i.PeriodKey = @period
 ORDER BY i.Id;
IF @instance IS NULL THROW 50465, 'pi-events-seed: table instance of the stand document not found', 1;

DECLARE @startColumn int, @volumeColumn int;
SELECT @startColumn = MIN(c.Id), @volumeColumn = MAX(c.Id)
  FROM (SELECT TOP (2) c.Id FROM cfg.ColumnDef AS c WHERE c.TableDefId = @table AND c.IsDeleted = 0 ORDER BY c.Id) AS c;
IF @startColumn IS NULL OR @startColumn = @volumeColumn THROW 50465, 'pi-events-seed: two columns of the stand table not found', 1;

DECLARE @user int = (SELECT TOP (1) Id FROM sec.[User] ORDER BY Id);

BEGIN TRANSACTION;

INSERT INTO ext.DataSource (Code, NameL10n, Transport, Endpoint, SecretName, IsActive, MaxParallel)
VALUES (N'E2E_PI_EF', N'{"en":"E2E PI events (stand)"}', 0, N'https://pi.e2e.test/piwebapi', N'e2e-pi-none', 1, 1);
DECLARE @source int = SCOPE_IDENTITY();

INSERT INTO ext.SourceEntity (DataSourceId, Code, DisplayName, EntityPath, IsActive, SourceKind, OnMissingInSource, ValidToInclusive)
VALUES (@source, N'E2E_FLARE_EVENTS', N'E2E flare events', NULL, 0, 0, 0, 0);
DECLARE @entity int = SCOPE_IDENTITY();

INSERT INTO ext.SourceEventMap (SourceEntityId, DocumentId, TableDefId, FilterAttribute, FilterScope, FilterValue, VolumeMode, IsActive)
VALUES (@entity, @document, @table, NULL, NULL, NULL, 0, 0);
DECLARE @map int = SCOPE_IDENTITY();

INSERT INTO ext.SourceEventFieldMap (SourceEventMapId, TargetColumnDefId, SourceAttribute, AttributeScope, ValueKind, SourceUnitId, TargetUnitId)
VALUES (@map, @startColumn, N'$start', 0, 0, NULL, NULL),
       (@map, @volumeColumn, N'Volume', 0, 0, NULL, NULL);

-- Записана подія (рядок EF-E2E-1), незакрита подія, подія за стелею рядків.
INSERT INTO ext.SourceEventLink
    (SourceEventMapId, SourceEventId, PeriodKey, TableInstanceId, RowKey, EventName, StartUtc, EndUtc, SourceModifiedUtc,
     PrimaryElement, [Status], KeptManualJson, UnmappedJson, FirstSeenAt, LastSeenAt, LastSyncAt)
VALUES (@map, N'E2E-1', @period, @instance, N'EF-E2E-1', N'E2E flaring one', DATEADD(DAY, -10, @now), DATEADD(HOUR, 1, DATEADD(DAY, -10, @now)), DATEADD(DAY, -9, @now),
        N'E2E-FLARE-1', N'Synced', NULL, NULL, DATEADD(DAY, -9, @now), @now, DATEADD(DAY, -1, @now)),
       (@map, N'E2E-2', NULL, NULL, NULL, N'E2E flaring open', DATEADD(DAY, -2, @now), NULL, DATEADD(DAY, -2, @now),
        N'E2E-FLARE-1', N'Open', NULL, NULL, DATEADD(DAY, -2, @now), @now, DATEADD(DAY, -1, @now)),
       (@map, N'E2E-3', NULL, NULL, NULL, N'E2E flaring limited', DATEADD(DAY, -5, @now), DATEADD(HOUR, 2, DATEADD(DAY, -5, @now)), DATEADD(DAY, -5, @now),
        N'E2E-FLARE-2', N'RowLimit', NULL, NULL, DATEADD(DAY, -5, @now), @now, DATEADD(DAY, -1, @now));

INSERT INTO ext.SourceEventAttributeValue (LinkId, Scope, Attribute, ValueNumeric, ValueText, IsTruncated, SourceUnit, ValueHash, SourceModifiedUtc, ChangedAt, RunId)
SELECT l.Id, 0, N'Volume', v.n, NULL, 0, N'Sm3', HASHBYTES('SHA2_256', CONCAT(l.Id, N'|', v.n)), l.SourceModifiedUtc, @now, 'e2e'
  FROM ext.SourceEventLink AS l
  JOIN (VALUES (N'E2E-1', 12.5), (N'E2E-2', 3.25), (N'E2E-3', 7)) AS v(id, n) ON v.id = l.SourceEventId
 WHERE l.SourceEventMapId = @map;

-- Журнал видалень повною звіркою: одна подія, якої в PI більше немає (склад — як пише синк).
INSERT INTO aud.StructureChange
    (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation, OldJson, NewJson, ChangeReason, ChangedByUserId, CorrelationId)
VALUES (DATEADD(DAY, -3, @now), 0, N'ext.SourceEventLink', 0, 1, N'SourceEventRowRemoved',
        CONCAT(N'{"eventId":"E2E-GONE","eventName":"E2E flaring removed","eventStartUtc":"2026-01-01T00:00:00Z","rowKey":"EF-E2E-GONE","rowId":0,',
               N'"periodKey":', @period, N',"tableInstanceId":', @instance, N',"documentId":', @document, N',"mapId":', @map,
               N',"sourceEntityId":', @entity, N',"linkId":0,"runId":"e2e","cells":[]}'),
        NULL, N'E2E: повна звірка подій (стенд)', @user, N'e2e');

COMMIT TRANSACTION;

SELECT @entity;
