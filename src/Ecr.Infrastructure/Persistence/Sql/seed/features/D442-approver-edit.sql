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
