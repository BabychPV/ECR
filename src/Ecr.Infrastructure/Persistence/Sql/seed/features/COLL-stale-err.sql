-- COLL:stale-err ── ru/kz resultsStale у переліку документів: відмови параметрів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.resultsStaleNeedsPeriod', N'ru', N'Для фильтра по устаревшим результатам нужен период: устаревание относится к периоду.'),
    (N'err.ECR-REQ-0422.resultsStaleNeedsPeriod', N'kz', N'Ескірген нәтижелер бойынша сүзу үшін кезең қажет: ескіру кезеңге қатысты.'),
    (N'err.ECR-REQ-0422.staleBy', N'ru', N'staleBy принимает только «me» и только вместе с resultsStale=true.'),
    (N'err.ECR-REQ-0422.staleBy', N'kz', N'staleBy тек «me» мәнін және тек resultsStale=true-мен бірге қабылдайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:stale-err ── кінець секції ──
