-- PERMITS: bindings ── ECR-PRM-0404 ── ru/kz: прив'язку аркуша не знайдено; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0404.binding', N'ru', N'Привязки разрешения «{bindingCode}» в этой версии шаблона нет.'),
    (N'err.ECR-PRM-0404.binding', N'kz', N'Үлгінің осы нұсқасында «{bindingCode}» рұқсат байланысы жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── ECR-PRM-0404 ── кінець секції ──

