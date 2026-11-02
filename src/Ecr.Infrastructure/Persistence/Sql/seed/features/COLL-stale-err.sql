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
-- COLL:stale-err ── resultsStale у переліку документів: відмови параметрів; ru/kz — порцією COLL:stale-err нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.resultsStaleNeedsPeriod', N'en', N'Filtering by stale results needs a period: staleness belongs to a period.', 1),
    (N'err.ECR-REQ-0422.staleBy', N'en', N'staleBy accepts only "me" and only together with resultsStale=true.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:stale-err ── кінець секції ──
