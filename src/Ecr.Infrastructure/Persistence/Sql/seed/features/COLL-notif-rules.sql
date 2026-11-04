-- COLL:notif-rules ── Матриця правил сповіщень: ознака незбереженої чернетки (аудит U14a) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'notifications.rulesUnsaved',             N'en', N'Unsaved changes', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:notif-rules ── кінець секції ──
