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
