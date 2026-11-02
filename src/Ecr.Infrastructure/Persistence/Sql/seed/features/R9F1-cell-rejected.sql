-- R9F1:cellRejected ── ru/kz: значення комірки збору відхилив обробник запису (F1-03); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'coverageEvents.cellRejected', N'ru', N'Ячейка {cell}: собранное значение отклонено при записи ({code}) и не записано; остальные поля записаны. Следующий прогон попробует снова.'),
    (N'coverageEvents.cellRejected', N'kz', N'{cell} ұяшығы: жиналған мән жазу кезінде қабылданбады ({code}) және жазылмады; қалған өрістер жазылды. Келесі іске қосу қайта көреді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- R9F1:cellRejected ── кінець секції ──

