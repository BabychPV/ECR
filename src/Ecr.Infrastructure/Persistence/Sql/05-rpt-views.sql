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
