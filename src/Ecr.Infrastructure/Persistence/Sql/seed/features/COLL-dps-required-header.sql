-- COLL:dps-required-header ── ru/kz D-PS: порожнє обов'язкове поле шапки блокує подання; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-HDR-0422.requiredAtSubmit', N'ru', N'Лист нельзя подать: обязательные поля шапки не заполнены: {headerFieldCodes}. Заполните их в шапке документа и подайте снова.'),
    (N'err.ECR-HDR-0422.requiredAtSubmit', N'kz', N'Парақты тапсыруға болмайды: тақырып бөлігінің міндетті өрістері толтырылмаған: {headerFieldCodes}. Оларды құжаттың тақырып бөлігінде толтырып, қайта тапсырыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:dps-required-header ── кінець секції ──
