-- HSE301:C5b ── відв'язка останнього джерела Formula-колонки опублікованої версії (D-215):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- 409 замість версії з колонкою, якої ніхто не рахує; джерела рахуються після зміни.
    (N'err.ECR-TMPL-4091', N'en', N'Column would lose its last source', 1),
    (N'err.ECR-TMPL-4091.lastSourceOfPublishedColumn', N'en', N'Column "{columnCode}" of published template version {templateVersion} would be left without a source: it has no template formula and no other active methodology binding. Bind the new source first, then deactivate this one.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:C5b ── кінець секції
