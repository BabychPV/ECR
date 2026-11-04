-- COLL:an77-recalc ── ru/kz: перерахунок усього документа без повного читання; перерахунок на поданому аркуші; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-AUTH-0403.recalcNeedsFullRead', N'ru', N'Пересчёт всего документа требует права чтения всех его листов. Пересчитайте отдельный лист или запросите полный доступ на чтение.'),
    (N'err.ECR-AUTH-0403.recalcNeedsFullRead', N'kz', N'Бүкіл құжатты қайта есептеу үшін оның барлық парақтарын оқу құқығы қажет. Жеке парақты қайта есептеңіз немесе толық оқу құқығын сұраңыз.'),
    (N'workflow.recalculateSheetSubmitted', N'ru', N'Лист {sheet} подан или утверждён, пересчитать его нельзя. Сначала верните лист на правки (действие листа «Переоткрыть для правок»).'),
    (N'workflow.recalculateSheetSubmitted', N'kz', N'{sheet} парағы тапсырылған немесе бекітілген, оны қайта есептеу мүмкін емес. Алдымен парақты түзетуге қайтарыңыз (парақтың «Түзету үшін қайта ашу» әрекеті).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an77-recalc ── кінець секції ──

-- COLL:an77-recalc ── N1-06/L8-12: перерахунок усього документа без повного читання; перерахунок на поданому аркуші; ru/kz — порцією COLL:an77-recalc нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-AUTH-0403.recalcNeedsFullRead', N'en', N'Recalculating the whole document needs read access to all of its sheets. Recalculate a single sheet instead, or ask for full read access.', 1),
    (N'workflow.recalculateSheetSubmitted', N'en', N'Sheet {sheet} is submitted or approved and cannot be recalculated. Return it for edits first (the sheet''s «Return for edits» action).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an77-recalc ── кінець секції ──
