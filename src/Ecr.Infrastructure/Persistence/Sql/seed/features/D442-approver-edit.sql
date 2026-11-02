-- D442:approver-edit ── ru/kz: позначка журналу «правка погоджувача» (D-442); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.originLabel.ApproverEdit', N'ru', N'Правка согласующего до утверждения'),
    (N'audit.originLabel.ApproverEdit', N'kz', N'Бекітуге дейінгі келісушінің түзетуі')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D442:approver-edit ── кінець секції ──
-- D442:approver-edit ── en: позначка журналу «правка погоджувача» (D-442, AN-129); ru/kz — порцією D442:approver-edit нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.originLabel.ApproverEdit', N'en', N'Approver''s edit before approval', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D442:approver-edit ── кінець секції ──
