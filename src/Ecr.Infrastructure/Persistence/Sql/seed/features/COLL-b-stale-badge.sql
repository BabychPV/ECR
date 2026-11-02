-- COLL:b-stale-badge ── ru/kz лінії B: бейдж застарілих результатів методологій; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.methodologyResultsStale', N'ru', N'Результаты методологий устарели'),
    (N'documents.methodologyResultsStale', N'kz', N'Әдістемелер нәтижелері ескірген'),
    (N'workflow.recalculateCalculations', N'ru', N'Пересчитать расчёты'),
    (N'workflow.recalculateCalculations', N'kz', N'Есептеулерді қайта есептеу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:b-stale-badge ── кінець секції ──
