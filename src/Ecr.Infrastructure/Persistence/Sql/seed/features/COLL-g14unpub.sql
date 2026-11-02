-- COLL:g14unpub ── ru/kz G14: методологія без опублікованих версій пропущена; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcMethodologyUnpublished', N'ru', N'Привязанные методологии без опубликованных версий пропущены: {count} (идентификаторы: {methodologies}). Опубликуйте их, чтобы они участвовали в расчёте; остальные методологии рассчитаны.'),
    (N'jobs.recalcMethodologyUnpublished', N'kz', N'Жарияланған нұсқасы жоқ байланыстырылған әдістемелер өткізіп жіберілді: {count} (идентификаторлары: {methodologies}). Есепке қосу үшін оларды жариялаңыз; қалған әдістемелер есептелді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:g14unpub ── кінець секції ──

