-- COLL:rc16lookup ── ru/kz: ціль Lookup-колонки/поля шапки - живий активний довідник; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.lookupRegistryUnknown', N'ru', N'Столбец «{columnCode}»: справочника {registryDefId} не существует или он неактивен.'),
    (N'err.ECR-TMPL-0422.lookupRegistryUnknown', N'kz', N'«{columnCode}» бағаны: {registryDefId} анықтамалығы жоқ немесе ол белсенді емес.'),
    (N'err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown', N'ru', N'Поле шапки «{headerFieldCode}»: справочника {registryDefId} не существует или он неактивен.'),
    (N'err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown', N'kz', N'«{headerFieldCode}» тақырып өрісі: {registryDefId} анықтамалығы жоқ немесе ол белсенді емес.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc16lookup ── кінець секції ──
-- COLL:rc16lookup ── RC16-2: ціль Lookup-колонки/поля шапки - живий активний довідник (збереження і публікація); ru/kz - порцією COLL:rc16lookup нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.lookupRegistryUnknown', N'en', N'Column "{columnCode}": registry {registryDefId} does not exist or is not active.', 1),
    (N'err.ECR-TMPL-0422.headerFieldLookupRegistryUnknown', N'en', N'Header field "{headerFieldCode}": registry {registryDefId} does not exist or is not active.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc16lookup ── кінець секції ──
