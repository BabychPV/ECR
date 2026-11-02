-- PERMITS: permits ── ru/kz: читання дозволів — фільтри переліку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0422.statusUnknown', N'ru', N'Статуса «{status}» не существует. Используйте Draft, Active или Expired.'),
    (N'err.ECR-PRM-0422.statusUnknown', N'kz', N'«{status}» күйі жоқ. Draft, Active немесе Expired қолданыңыз.'),
    (N'err.ECR-PRM-0422.cursorInvalid', N'ru', N'Курсор страницы списка разрешений некорректен. Начните с первой страницы.'),
    (N'err.ECR-PRM-0422.cursorInvalid', N'kz', N'Рұқсаттар тізімі бетінің курсоры жарамсыз. Бірінші беттен бастаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: permits ── читання ── ru/kz ── кінець секції ──

