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

-- COLL:an121-calendar-offset ── AN-121 / C1-03: календарний контекст іншого періоду ([Period:-1].Days) не підтримується; ru/kz — порцією COLL:an121-calendar-offset нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'expr.calendarContextOffset', N'en', N'The calendar context of another period is not supported: write "[Period].Property".', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an121-calendar-offset ── кінець секції ──
