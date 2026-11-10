-- FV814:where-used ── ФВ-8.14 «Де використовується»: правила валідації, правила довідників, дані колонки ── ru/kz; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'usageKind.validationRule', N'ru', N'Правило проверки шаблона'),
    (N'usageKind.validationRule', N'kz', N'Үлгіні тексеру ережесі'),
    (N'usageKind.registryRule', N'ru', N'Правило справочника'),
    (N'usageKind.registryRule', N'kz', N'Анықтамалық ережесі'),
    (N'columns.usageDataInDocuments', N'ru', N'Значения этого столбца уже хранятся в документах'),
    (N'columns.usageDataInDocuments', N'kz', N'Бұл бағанның мәндері құжаттарда бұрыннан сақталған')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- FV814:where-used ── кінець секції ──
-- FV814:where-used ── ФВ-8.14 «Де використовується» ── en; ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'usageKind.validationRule', N'en', N'Template validation rule', 1),
    (N'usageKind.registryRule', N'en', N'Registry rule', 1),
    (N'columns.usageDataInDocuments', N'en', N'Values of this column are already stored in documents', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- FV814:where-used ── кінець секції ──
