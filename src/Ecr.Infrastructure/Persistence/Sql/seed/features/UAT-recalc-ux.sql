-- UAT:recalc-ux ── ru/kz: перерахунок без глухого кута (пропуск лише затверджених аркушів, позначка «перераховано після подання»), стан «частково подано» ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcSheetLog', N'ru', N'Пропущено утверждённых листов: {skipped}. Пересчитано поданных листов (отмечены для согласующего): {recalculatedSubmitted}.'),
    (N'recalc.skippedSummary', N'ru', N'Пропущено утверждённых листов: {count} — их числа не изменены'),
    (N'recalc.skippedTruncated', N'ru', N'Показаны только первые {shown}.'),
    (N'recalc.nothingToRecalculate', N'ru', N'Пересчитывать нечего: все листы утверждены. Утверждённые числа не пересчитываются.'),
    (N'documents.statePartlySubmitted', N'ru', N'Подан частично'),
    (N'status.sheet.PartlySubmitted', N'ru', N'Подан частично'),
    (N'sheet.recalculatedAfterSubmission', N'ru', N'Пересчитано после подачи'),
    (N'sheet.recalculatedAfterSubmissionHint', N'ru', N'числа этого листа изменились после подачи. Проверьте их перед утверждением.'),
    (N'jobs.recalcSheetLog', N'kz', N'Бекітілген парақтар өткізілді: {skipped}. Берілген парақтар қайта есептелді (келісушіге белгіленді): {recalculatedSubmitted}.'),
    (N'recalc.skippedSummary', N'kz', N'Бекітілген парақтар өткізілді: {count} — олардың сандары өзгертілмеді'),
    (N'recalc.skippedTruncated', N'kz', N'Тек алғашқы {shown} көрсетілген.'),
    (N'recalc.nothingToRecalculate', N'kz', N'Қайта есептейтін ештеңе жоқ: барлық парақтар бекітілген. Бекітілген сандар қайта есептелмейді.'),
    (N'documents.statePartlySubmitted', N'kz', N'Ішінара берілген'),
    (N'status.sheet.PartlySubmitted', N'kz', N'Ішінара берілген'),
    (N'sheet.recalculatedAfterSubmission', N'kz', N'Берілгеннен кейін қайта есептелді'),
    (N'sheet.recalculatedAfterSubmissionHint', N'kz', N'бұл парақтың сандары берілгеннен кейін өзгерді. Бекітер алдында тексеріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:recalc-ux ── кінець секції ──
-- UAT:recalc-ux ── перерахунок без глухого кута, стан «частково подано» (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcSheetLog', N'en', N'Approved sheets skipped: {skipped}. Submitted sheets recalculated and marked for the approver: {recalculatedSubmitted}.', 1),
    (N'recalc.skippedSummary', N'en', N'Approved sheets skipped: {count} — their numbers were not changed', 1),
    (N'recalc.skippedTruncated', N'en', N'Only the first {shown} are listed.', 1),
    (N'recalc.nothingToRecalculate', N'en', N'Nothing to recalculate: all sheets are approved. Approved numbers are never recalculated.', 1),
    (N'documents.statePartlySubmitted', N'en', N'Partly submitted', 1),
    (N'status.sheet.PartlySubmitted', N'en', N'Partly submitted', 1),
    (N'sheet.recalculatedAfterSubmission', N'en', N'Recalculated after submission', 1),
    (N'sheet.recalculatedAfterSubmissionHint', N'en', N'numbers on this sheet changed after it was submitted. Review them before approving.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:recalc-ux ── кінець секції ──
