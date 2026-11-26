-- ROW:label-messages ── ru/kz integ-row-label-messages: службовий ключ рядка (GUID) у тексті для людини — позначка замість ключа ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'row.addedByUser', N'ru', N'(добавлена пользователем)'),
    (N'row.addedByUser', N'kz', N'(пайдаланушы қосқан)')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- ROW:label-messages ── кінець секції ──
-- ROW:label-messages ── en; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'row.addedByUser', N'en', N'(added by a user)', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- ROW:label-messages ── кінець секції ──
