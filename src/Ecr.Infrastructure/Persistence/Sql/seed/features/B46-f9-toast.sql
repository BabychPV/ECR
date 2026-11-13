-- B46:f9-toast ── ru/kz: тост F9 / «Recalculate» без сирого ідентифікатора задачі (живий прохід W15s); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.sheetRecalcQueued', N'ru', N'Пересчёт листа поставлен в очередь.'),
    (N'workflow.sheetRecalcQueued', N'kz', N'Парақты қайта есептеу кезекке қойылды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B46:f9-toast ── кінець секції ──

-- B46:f9-toast ── тост постановки перерахунку аркуша без id задачі; ru/kz — порцією B46:f9-toast вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'workflow.sheetRecalcQueued', N'en', N'Sheet recalculation queued.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B46:f9-toast ── кінець секції ──

-- B46:f9-toast ── прибрані ключі: «… queued as job {job}» показував сирий jobId; замінено на workflow.sheetRecalcQueued ──
INSERT INTO #UiRemoved ([Key], Lang, OldVal)
SELECT v.[Key], v.Lang, v.OldVal
  FROM (VALUES
    (N'workflow.recalcQueued', N'en', N'Recalculation queued as job {job}.'),
    (N'workflow.recalcQueued', N'ru', N'Пересчёт поставлен в очередь как задача {job}.'),
    (N'workflow.recalcQueued', N'kz', N'Қайта есептеу {job} тапсырмасы ретінде кезекке қойылды.')
       ) AS v ([Key], Lang, OldVal)
OPTION (RECOMPILE);
GO
-- B46:f9-toast ── кінець секції ──
