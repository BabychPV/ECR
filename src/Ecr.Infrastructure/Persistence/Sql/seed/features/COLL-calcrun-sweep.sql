-- COLL:calcrun-sweep ── Покинутий прогін розрахунку (P3, ФВ-9.8, `AbandonedWorkSweeper`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- `AbandonedWorkSweeper.AbandonedCalculationRunReason`: `calc.CalculationRun` у `Running`, який ніхто не закрив.
    (N'jobs.calculationRunAbandoned',           N'en', N'The calculation run was not closed: the process running it stopped before the run recorded its result. Start the recalculation again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:calcrun-sweep ── кінець секції ──
