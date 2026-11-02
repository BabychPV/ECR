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
-- COLL:dps-required-header ── D-PS: подання з порожнім обов'язковим полем шапки (SubmitSheetHandler); ru/kz — порцією COLL:dps-required-header у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-HDR-0422.requiredAtSubmit', N'en', N'The sheet cannot be submitted: required header field(s) are empty: {headerFieldCodes}. Fill them in the document header and submit again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:dps-required-header ── кінець секції ──
