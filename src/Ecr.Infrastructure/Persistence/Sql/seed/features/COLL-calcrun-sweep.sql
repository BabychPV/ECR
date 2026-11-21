-- COLL:calcrun-sweep ── Покинутий прогін розрахунку (P3, ФВ-9.8, `AbandonedWorkSweeper`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- `AbandonedWorkSweeper.AbandonedCalculationRunReason`: `calc.CalculationRun` у `Running`, який ніхто не закрив.
    (N'jobs.calculationRunAbandoned',           N'en', N'The calculation run was not closed: the process running it stopped before the run recorded its result. Start the recalculation again.', 1),
    -- L1b / D4 (`RecalculationJob.CancelledRunReason`): задачу скасовано (кнопка, межа тривалості, зупинка служби) посеред прогону.
    (N'jobs.calculationRunCancelled',           N'en', N'The calculation run was stopped: its job was cancelled before the run finished. The previous results stay current.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:calcrun-sweep ── кінець секції ──

-- COLL:calcrun-sweep ── ru/kz: L1b / D4, прогін скасованої задачі ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.calculationRunCancelled', N'ru', N'Запуск расчёта остановлен: его задачу отменили до завершения. Предыдущие результаты остаются актуальными.'),
    (N'jobs.calculationRunCancelled', N'kz', N'Есептеу іске қосуы тоқтатылды: оның тапсырмасы аяқталғанға дейін болдырылмады. Алдыңғы нәтижелер өзекті болып қалады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:calcrun-sweep ── кінець секції ──
