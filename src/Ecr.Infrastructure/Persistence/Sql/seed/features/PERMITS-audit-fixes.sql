-- PERMITS: audit-fixes ── ru/kz: аудит ч.1, причина вимкненої «Publish…» без права; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.editor.actions.publishNoRight', N'ru', N'Для публикации типов разрешений нужно право на публикацию. Попросите администратора выдать его.'),
    (N'permits.editor.actions.publishNoRight', N'kz', N'Рұқсат түрлерін жариялау үшін жариялау құқығы қажет. Әкімшіден оны беруді сұраңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: audit-fixes ── кінець секції ──
-- PERMITS: audit-fixes ── аудит ч.1: причина вимкненої «Publish…» без права; ru/kz — порцією PERMITS: audit-fixes нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.editor.actions.publishNoRight', N'en', N'Publishing permit types needs the publish right. Ask an administrator to grant it.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: audit-fixes ── кінець секції ──
