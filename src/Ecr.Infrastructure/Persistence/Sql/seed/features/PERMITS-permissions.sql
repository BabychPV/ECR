-- PERMITS: permissions ── ru/kz: підписи прав типів дозволів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permission.Permit.EditType', N'ru', N'Изменение типов разрешений'),
    (N'permission.Permit.EditType', N'kz', N'Рұқсат түрлерін өңдеу'),
    (N'permission.Permit.PublishType', N'ru', N'Публикация типов разрешений'),
    (N'permission.Permit.PublishType', N'kz', N'Рұқсат түрлерін жариялау'),
    (N'security.domain.Permit', N'ru', N'Разрешения'),
    (N'security.domain.Permit', N'kz', N'Рұқсаттар')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: permissions ── ru/kz ── кінець секції ──

