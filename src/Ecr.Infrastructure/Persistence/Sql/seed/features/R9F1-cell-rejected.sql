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

-- R9F1:cellRejected ── F1-03: значення комірки збору відхилив обробник запису, решту полів записано (`CoverageDetails.CellRejected`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'coverageEvents.cellRejected',            N'en', N'Cell {cell}: the collected value was rejected on write ({code}) and was not written; the other fields were written. The next run will try again.', 1)
    -- ru/kz — окремою порцією `R9F1:cellRejected` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- R9F1:cellRejected ── кінець секції ──
