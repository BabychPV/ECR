-- COLL:rawhealth ── ru/kz розміру ext.RawDataPoint у картці `db`; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.collection.rawPointsApproaching', N'ru', N'В ext.RawDataPoint около {rows} строк: приближается порог пересмотра R2 ({threshold}).'),
    (N'health.collection.rawPointsApproaching', N'kz', N'ext.RawDataPoint кестесінде шамамен {rows} жол бар: R2 қайта қарау шегіне ({threshold}) жақындап келеді.'),
    (N'health.collection.rawPointsOverThreshold', N'ru', N'В ext.RawDataPoint около {rows} строк: порог пересмотра R2 ({threshold}) превышен. См. docs/build/perf/R2-rawdatapoint-plan.md.'),
    (N'health.collection.rawPointsOverThreshold', N'kz', N'ext.RawDataPoint кестесінде шамамен {rows} жол бар: R2 қайта қарау шегі ({threshold}) асып кетті. docs/build/perf/R2-rawdatapoint-plan.md қараңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rawhealth ── кінець секції ──

