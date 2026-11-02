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

-- PERMITS: permissions ── en: підписи прав типів дозволів ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permission.Permit.EditType', N'en', N'Edit permit types', 1),
    (N'permission.Permit.PublishType', N'en', N'Publish permit types', 1),
    (N'security.domain.Permit', N'en', N'Permits', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: permissions ── en ── кінець секції ──
