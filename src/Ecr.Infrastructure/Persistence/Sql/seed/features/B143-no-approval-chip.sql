-- B143:no-approval-chip ── ru/kz: позначка «Без погодження» в рядку прогресу документа замість стану аркуша (B75: шаблон без подання й затвердження, workflowMode = None); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.noApproval', N'ru', N'Без согласования'),
    (N'workflow.noApproval', N'kz', N'Келісусіз'),
    (N'workflow.noApprovalHint', N'ru', N'Этот шаблон работает без подачи и утверждения: значения доступны отчётности сразу после сохранения. Строки защищаются блокировками строк.'),
    (N'workflow.noApprovalHint', N'kz', N'Бұл үлгі тапсырусыз және бекітусіз жұмыс істейді: мәндер сақталғаннан кейін бірден есептілікке қолжетімді. Жолдар жол бұғаттарымен қорғалады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B143:no-approval-chip ── кінець секції ──

-- B143:no-approval-chip ── en: позначка «Без погодження» (B75); ru/kz — порцією B143:no-approval-chip вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.noApproval', N'en', N'No approval', 1),
    (N'workflow.noApprovalHint', N'en', N'This template works without submission and approval: values are available to reporting right after saving. Rows are protected by row locks.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B143:no-approval-chip ── кінець секції ──
