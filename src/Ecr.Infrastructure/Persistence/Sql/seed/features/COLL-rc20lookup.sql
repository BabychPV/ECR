-- COLL:rc20lookup ── ru/kz R16-1: Lookup-колонка/поле шапки без довідника; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.lookupRegistryRequired', N'ru', N'Столбец «{columnCode}» имеет тип «Выбор из справочника» и требует справочник: выберите его.'),
    (N'err.ECR-TMPL-0422.lookupRegistryRequired', N'kz', N'«{columnCode}» бағаны «Анықтамалықтан таңдау» түрінде және анықтамалықты қажет етеді: оны таңдаңыз.'),
    (N'err.ECR-TMPL-0422.headerFieldLookupRegistryRequired', N'ru', N'Поле шапки «{headerFieldCode}» имеет тип «Выбор из справочника» и требует справочник: выберите его.'),
    (N'err.ECR-TMPL-0422.headerFieldLookupRegistryRequired', N'kz', N'«{headerFieldCode}» тақырып өрісі «Анықтамалықтан таңдау» түрінде және анықтамалықты қажет етеді: оны таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc20lookup ── кінець секції ──
