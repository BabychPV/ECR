-- src/Ecr.Infrastructure/Persistence/Sql/02-partitions.sql
-- PeriodKey = Year*100 + Sequence (R-A6). Одна партиція = один період.
-- Межі виписуються наперед; PartitionCheckJob стежить за запасом на 6 періодів.
CREATE PARTITION FUNCTION pf_ByPeriodKey (int)
AS RANGE RIGHT FOR VALUES
(
    202601, 202602, 202603, 202604, 202605, 202606,
    202607, 202608, 202609, 202610, 202611, 202612,
    202701, 202702, 202703, 202704, 202705, 202706,
    202707, 202708, 202709, 202710, 202711, 202712
);
GO

CREATE PARTITION SCHEME ps_ByPeriodKey
AS PARTITION pf_ByPeriodKey ALL TO ([DATA_HOT]);
GO

-- Аудит партиціонується ОКРЕМО, по ChangedAt: місяць зміни і звітний період —
-- різні осі (зміна за січень може статися в березні).
CREATE PARTITION FUNCTION pf_AuditByMonth (datetime2(3))
AS RANGE RIGHT FOR VALUES
(
    '2026-01-01', '2026-02-01', '2026-03-01', '2026-04-01', '2026-05-01', '2026-06-01',
    '2026-07-01', '2026-08-01', '2026-09-01', '2026-10-01', '2026-11-01', '2026-12-01',
    '2027-01-01', '2027-02-01', '2027-03-01', '2027-04-01', '2027-05-01', '2027-06-01'
);
GO

CREATE PARTITION SCHEME ps_AuditByMonth
AS PARTITION pf_AuditByMonth ALL TO ([AUDIT]);
GO
