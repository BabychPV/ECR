-- COLL:approval-queue ── ru/kz: ФВ-12.5, черга «До погодження»; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.approvalQueueCursorInvalid', N'ru', N'Курсор страницы очереди согласования недействителен. Обновите список.'),
    (N'err.ECR-REQ-0422.approvalQueueCursorInvalid', N'kz', N'Келісу кезегі бетінің курсоры жарамсыз. Тізімді жаңартыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:approval-queue ── кінець секції ──

