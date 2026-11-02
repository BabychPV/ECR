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

