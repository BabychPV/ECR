-- COLL:r7y4-run-running ── ru/kz: R7-Y4 / Y4-03: прогін збору, що ще триває; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'status.collectionRun.Running', N'ru', N'Выполняется'),
    (N'status.collectionRun.Running', N'kz', N'Орындалуда')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y4-run-running ── кінець секції ──

-- COLL:r7y4-run-running ── R7-Y4 / Y4-03: прогін збору, що ще триває, на /admin/sources; ru/kz — порцією COLL:r7y4-run-running у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'status.collectionRun.Running', N'en', N'Running', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y4-run-running ── кінець секції ──
