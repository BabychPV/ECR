-- COLL:p3-audit-key ── ru/kz зняття залежності розкладу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'integrationAudit.scheduleDependencyCleared', N'ru', N'Зависимость расписания сбора {id} снята: расписание {deleted} сущности «{entity}» удалено.'),
    (N'integrationAudit.scheduleDependencyCleared', N'kz', N'{id} жинау жоспарының тәуелділігі алынды: «{entity}» нысанының {deleted} жоспары жойылды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-audit-key ── кінець секції ──
