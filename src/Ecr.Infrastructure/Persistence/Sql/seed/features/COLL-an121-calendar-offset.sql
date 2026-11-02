-- COLL:an121-calendar-offset ── ru/kz: AN-121 / C1-03: календарний контекст іншого періоду не підтримується; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'expr.calendarContextOffset', N'ru', N'Календарный контекст другого периода не поддерживается: пишите «[Period].Property».'),
    (N'expr.calendarContextOffset', N'kz', N'Басқа кезеңнің күнтізбелік контексіне қолдау көрсетілмейді: «[Period].Property» түрінде жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an121-calendar-offset ── кінець секції ──

