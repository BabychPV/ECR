-- PERMITS: midmonth-split-audit ── ru/kz: фіча (j) P4 09.11 — підписи журналу Audit: історія дозволу (PermitSegment), поділ місяця (CellPermitPart), операції Add/Remove/Split; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.entityLabel.PermitSegment', N'ru', N'История разрешения'),
    (N'audit.entityLabel.PermitSegment', N'kz', N'Рұқсат тарихы'),
    (N'audit.entityLabel.CellPermitPart', N'ru', N'Разделение месяца между разрешениями'),
    (N'audit.entityLabel.CellPermitPart', N'kz', N'Айды рұқсаттар арасында бөлу'),
    (N'audit.operationLabel.Add', N'ru', N'Добавление'),
    (N'audit.operationLabel.Add', N'kz', N'Қосу'),
    (N'audit.operationLabel.Remove', N'ru', N'Удаление'),
    (N'audit.operationLabel.Remove', N'kz', N'Жою'),
    (N'audit.operationLabel.Split', N'ru', N'Разделение'),
    (N'audit.operationLabel.Split', N'kz', N'Бөлу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: midmonth-split-audit ── кінець секції ──
-- PERMITS: midmonth-split-audit ── en: фіча (j) P4 09.11 — підписи журналу Audit; ru/kz — порцією PERMITS: midmonth-split-audit вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.entityLabel.PermitSegment', N'en', N'Permit history', 1),
    (N'audit.entityLabel.CellPermitPart', N'en', N'Month split between permits', 1),
    (N'audit.operationLabel.Add', N'en', N'Add', 1),
    (N'audit.operationLabel.Remove', N'en', N'Remove', 1),
    (N'audit.operationLabel.Split', N'en', N'Split', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: midmonth-split-audit ── кінець секції ──
