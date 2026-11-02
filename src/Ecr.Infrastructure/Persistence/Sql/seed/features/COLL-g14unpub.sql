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

-- COLL:g14unpub ── G14: привʼязана методологія без опублікованих версій пропускається; ru/kz — порцією COLL:g14unpub нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcMethodologyUnpublished', N'en', N'{count} bound methodology(ies) have no published version and were skipped (methodology ids: {methodologies}). Publish them to include them in the calculation; the other methodologies were calculated.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:g14unpub ── кінець секції ──
