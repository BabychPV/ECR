-- RECALC:periods-status ── ru/kz правдивого стану перерахунку проєкту (P4 ФВ-9.8); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'workflow.recalcDoneWithErrors', N'ru', N'Пересчёт не завершён: выполнено {done} из {total} документов, ошибок {failed}. Показатели документов с ошибками не актуальны.'),
    (N'workflow.recalcDoneWithErrors', N'kz', N'Қайта есептеу аяқталмады: {total} құжаттың {done} орындалды, қате {failed}. Қатесі бар құжаттардың көрсеткіштері өзекті емес.'),
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
    (N'workflow.recalcDoneWithErrors',          N'en', N'Recalculation is not complete: done {done} of {total} documents, errors {failed}. Figures of the failed documents are not up to date.', 1),
    (N'status.job.FannedOut',                   N'en', N'Calculating documents', 1),
    (N'status.job.SucceededWithErrors',         N'en', N'Done with errors', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- RECALC:periods-status ── кінець секції ──
