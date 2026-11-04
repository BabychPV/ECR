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

-- COLL:r5c1-issues-more ── R5-C1 / C1-02: інспектор показує зауваження порціями — кнопка «ще»; ru/kz — порцією COLL:r5c1-issues-more нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'inspector.showMoreIssues', N'en', N'Show {count} more ({left} not shown)', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r5c1-issues-more ── кінець секції ──
