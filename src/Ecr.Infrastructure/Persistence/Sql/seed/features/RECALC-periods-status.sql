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
