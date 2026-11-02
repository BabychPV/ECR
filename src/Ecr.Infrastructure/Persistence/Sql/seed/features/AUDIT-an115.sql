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

