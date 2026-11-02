-- D440:approved-by-submitter ── ru/kz: позначка «погоджено автором подання» в історії аркуша (D-440); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.historyApprovedBySubmitter', N'ru', N'утверждено автором подачи'),
    (N'workflow.historyApprovedBySubmitter', N'kz', N'тапсырған автор бекітті')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D440:approved-by-submitter ── кінець секції ──
