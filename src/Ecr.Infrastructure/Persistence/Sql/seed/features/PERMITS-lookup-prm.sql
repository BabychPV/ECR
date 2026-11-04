-- PERMITS: lookup-prm ── ru/kz: шапка типу дозволу як ціль Lookup поля шапки й колонки шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'registries.permitHeaderOption', N'ru', N'Разрешения: {name} ({code})'),
    (N'registries.permitHeaderOption', N'kz', N'Рұқсаттар: {name} ({code})'),
    (N'registries.lookupGroup.permits', N'ru', N'Разрешения'),
    (N'registries.lookupGroup.permits', N'kz', N'Рұқсаттар'),
    (N'registries.lookupGroup.dictionaries', N'ru', N'Справочники'),
    (N'registries.lookupGroup.dictionaries', N'kz', N'Анықтамалықтар')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: lookup-prm ── кінець секції ──
-- PERMITS: lookup-prm ── en: шапка типу дозволу як ціль Lookup поля шапки й колонки шаблону; ru/kz — порцією PERMITS: lookup-prm нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'registries.permitHeaderOption', N'en', N'Permits: {name} ({code})', 1),
    (N'registries.lookupGroup.permits', N'en', N'Permits', 1),
    (N'registries.lookupGroup.dictionaries', N'en', N'Dictionaries', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: lookup-prm ── кінець секції ──
