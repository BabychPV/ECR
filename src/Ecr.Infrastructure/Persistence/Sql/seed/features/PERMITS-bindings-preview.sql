-- PERMITS: bindings ── preview ── ru/kz: перегляд прив'язки без періоду; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-PRM-0422.bindingPreviewPeriodRequired', N'ru', N'Просмотр из документа требует период (periodKey).'),
    (N'err.ECR-PRM-0422.bindingPreviewPeriodRequired', N'kz', N'Құжаттан қарау кезеңді (periodKey) қажет етеді.'),
    (N'err.ECR-PRM-0422.bindingPreviewPeriodUnknown', N'ru', N'В проекте документа нет периода {periodKey}.'),
    (N'err.ECR-PRM-0422.bindingPreviewPeriodUnknown', N'kz', N'Құжат жобасында {periodKey} кезеңі жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: bindings ── preview ── кінець секції ──
