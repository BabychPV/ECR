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
