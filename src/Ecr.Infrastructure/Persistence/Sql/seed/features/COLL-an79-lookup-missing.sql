-- COLL:an79-lookup-missing ── ru/kz: AN-79 / N1-07/N1-08: запис довідника поля шапки Lookup відсутній у довіднику цього поля; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-HDR-0422.lookupEntryMissing', N'ru', N'Запись справочника, выбранная для поля шапки «{headerFieldCode}», отсутствует в справочнике этого поля.'),
    (N'err.ECR-HDR-0422.lookupEntryMissing', N'kz', N'«{headerFieldCode}» тақырып бөлігі өрісі үшін таңдалған анықтамалық жазбасы осы өрістің анықтамалығында жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an79-lookup-missing ── кінець секції ──

