-- HSE301:A1P1 ── стеля констант версії відмовляє, а не обрізає мовчки (аудит P1):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- прогін читає всі константи версії одним запитом, і відкинутий хвіст дав би тихий #REF.
    (N'err.ECR-CALC-0422.constantsOverCap', N'en', N'Methodology version {methodologyVersionId} has more than {cap} constants: they cannot all be read, and a constant left out would silently evaluate to #REF.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:A1P1 ── кінець секції
