-- AUDIT:an115 ── ru/kz автора системної зміни комірки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.systemAuthor', N'ru', N'Система'),
    (N'audit.systemAuthor', N'kz', N'Жүйе')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- AUDIT:an115 ── кінець секції ──

-- AUDIT:an115 ── автор системної зміни комірки (перерахунок, інтеграція, міграція); ru/kz — порцією AUDIT:an115 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.systemAuthor', N'en', N'System', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- AUDIT:an115 ── кінець секції ──
