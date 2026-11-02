-- COLL:p4-t4 ── ru/kz тестувального проходу №4 (T4-09); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.preferenceLanguageUnsupported', N'ru', N'Язык настройки «{key}» должен быть кодом включённого языка интерфейса (например «en», «ru» или «kz»), получено: {value}.'),
    (N'err.ECR-REQ-0422.preferenceLanguageUnsupported', N'kz', N'«{key}» баптауының тілі қосылған интерфейс тілінің коды болуы тиіс (мысалы, «en», «ru» немесе «kz»), алынғаны: {value}.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p4-t4 ── кінець секції ──
-- COLL:p4-t4 ── тестувальний прохід №4, T4-09: мова налаштування поза реєстром; ru/kz — порцією COLL:p4-t4 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.preferenceLanguageUnsupported', N'en', N'The language of preference "{key}" must be the code of an enabled interface language (for example "en", "ru" or "kz"), got: {value}.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p4-t4 ── кінець секції ──
