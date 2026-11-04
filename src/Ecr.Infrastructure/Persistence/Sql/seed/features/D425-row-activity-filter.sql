-- D425:row-activity-filter ── ru/kz: помилка формату фільтра активності рядків (en — у блоці err.ECR-CALC-0422); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CALC-0422.rowActivityFilterFormat', N'ru', N'Фильтр активности строк должен иметь вид КОД_СПРАВОЧНИКА:КОД_ЗАПИСИ, например LAND_GENERIC_EQUIPSTATUS:Running.'),
    (N'err.ECR-CALC-0422.rowActivityFilterFormat', N'kz', N'Жолдардың белсенділік сүзгісі КОД_АНЫҚТАМАЛЫҚ:КОД_ЖАЗБА түрінде болуы керек, мысалы LAND_GENERIC_EQUIPSTATUS:Running.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D425:row-activity-filter ── кінець секції ──

