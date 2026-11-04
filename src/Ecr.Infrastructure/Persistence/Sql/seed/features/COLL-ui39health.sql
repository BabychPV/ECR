-- COLL:ui39health ── ru/kz UI-39: сторінка Health за макетом; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'health.subtitle', N'ru', N'Работает ли ECR прямо сейчас? Каждая часть проверяется автоматически; предупреждение появляется, только когда нужно что-то сделать.'),
    (N'health.subtitle', N'kz', N'ECR қазір жұмыс істеп тұр ма? Әр бөлік автоматты түрде тексеріледі; ескерту тек бірдеңе істеу қажет болғанда шығады.'),
    (N'health.checkNow', N'ru', N'Проверить сейчас'),
    (N'health.checkNow', N'kz', N'Қазір тексеру'),
    (N'health.checkedNow', N'ru', N'Проверено только что.'),
    (N'health.checkedNow', N'kz', N'Жаңа ғана тексерілді.'),
    (N'health.copyDiagnostics', N'ru', N'Копировать диагностику'),
    (N'health.copyDiagnostics', N'kz', N'Диагностиканы көшіру'),
    (N'health.diagnosticsCopied', N'ru', N'Диагностика скопирована в буфер обмена. В ней нет паролей и персональных данных.'),
    (N'health.diagnosticsCopied', N'kz', N'Диагностика алмасу буферіне көшірілді. Онда құпиясөздер мен жеке деректер жоқ.'),
    (N'health.banner.title', N'ru', N'{check}: требуется внимание'),
    (N'health.banner.title', N'kz', N'{check}: назар аудару қажет'),
    (N'health.durationMs', N'ru', N'ответ {ms} мс'),
    (N'health.durationMs', N'kz', N'жауап {ms} мс'),
    (N'health.checkedAt', N'ru', N'Проверено'),
    (N'health.checkedAt', N'kz', N'Тексерілді')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui39health ── кінець секції ──
-- COLL:ui39health ── UI-39: сторінка Health за макетом (банер, «Check now», «Copy diagnostics»); ru/kz — порцією COLL:ui39health у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'health.subtitle', N'en', N'Is ECR working right now? Each part is checked automatically; a warning appears only when there is something to do.', 1),
    (N'health.checkNow', N'en', N'Check now', 1),
    (N'health.checkedNow', N'en', N'Checked just now.', 1),
    (N'health.copyDiagnostics', N'en', N'Copy diagnostics', 1),
    (N'health.diagnosticsCopied', N'en', N'Diagnostics copied to the clipboard. They contain no passwords or personal data.', 1),
    (N'health.banner.title', N'en', N'{check} needs attention', 1),
    (N'health.durationMs', N'en', N'{ms} ms response', 1),
    (N'health.checkedAt', N'en', N'Checked', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui39health ── кінець секції ──
