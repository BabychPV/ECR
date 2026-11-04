-- Q17c: sweepJobKind ── ru/kz: Q-17, підпис задачі прибирання витіснених результатів розрахунку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.kind.supersededResultsSweep', N'ru', N'Очистка вытесненных результатов расчёта'),
    (N'jobs.kind.supersededResultsSweep', N'kz', N'Ығыстырылған есептеу нәтижелерін тазарту')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- Q17c: sweepJobKind ── кінець секції ──
-- Q17c: sweepJobKind ── Q-17: підпис типу фонової задачі SupersededResultsSweepJob у /admin/jobs; ru/kz — порцією Q17c нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.kind.supersededResultsSweep', N'en', N'Superseded calculation results cleanup', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- Q17c: sweepJobKind ── кінець секції ──
