-- COLL:an123-save-busy ── ru/kz: AN-123 / R1-03 = R2-01: автозбереження чекає, доки дані звільняться; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.saveWaitingBusy', N'ru', N'Данные заняты, сохраним автоматически...'),
    (N'grid.saveWaitingBusy', N'kz', N'Деректер бос емес, автоматты түрде сақтаймыз...')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an123-save-busy ── кінець секції ──
