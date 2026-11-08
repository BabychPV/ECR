-- RECALC:periods-status ── ru/kz правдивого стану перерахунку проєкту (P4 ФВ-9.8); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'status.job.FannedOut', N'ru', N'Документы пересчитываются'),
    (N'status.job.FannedOut', N'kz', N'Құжаттар қайта есептелуде'),
    (N'status.job.SucceededWithErrors', N'ru', N'Выполнено с ошибками'),
    (N'status.job.SucceededWithErrors', N'kz', N'Қателермен орындалды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- RECALC:periods-status ── кінець секції ──
-- RECALC:periods-status ── Правдивий стан перерахунку проєкту на екранах (P4 ФВ-9.8: «завершено» лише коли M = N і K = 0) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'status.job.FannedOut',                   N'en', N'Calculating documents', 1),
    (N'status.job.SucceededWithErrors',         N'en', N'Done with errors', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RECALC:periods-status ── кінець секції ──
