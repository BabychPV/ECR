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
-- D440:approved-by-submitter ── en: позначка в історії аркуша «погоджено автором подання» (D-440, AN-127); ru/kz — порцією D440:approved-by-submitter нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.historyApprovedBySubmitter', N'en', N'approved by the submitter', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D440:approved-by-submitter ── кінець секції ──
