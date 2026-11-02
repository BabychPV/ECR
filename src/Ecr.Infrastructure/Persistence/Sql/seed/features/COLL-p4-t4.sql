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
