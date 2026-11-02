-- COLL:r5c1-issues-more ── ru/kz: R5-C1 / C1-02: зауваження інспектора порціями; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'inspector.showMoreIssues', N'ru', N'Показать ещё {count} (не показано: {left})'),
    (N'inspector.showMoreIssues', N'kz', N'Тағы {count} көрсету (көрсетілмегені: {left})')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r5c1-issues-more ── кінець секції ──

