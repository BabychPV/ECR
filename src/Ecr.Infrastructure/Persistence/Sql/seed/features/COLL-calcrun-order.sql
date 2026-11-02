-- COLL:calcrun-order ── Старіший прогін не перекриває новіший (борг P4, `CalculationResultStore.SwitchCurrentRunAsync`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Причина в `calc.CalculationRun.ErrorMessage` прогону, що завершився після новішого тієї ж області: `{runId}` — новіший, актуальний.
    (N'jobs.calculationRunSupersededByNewer',   N'en', N'The run finished after a newer run {runId} of the same scope and did not replace its results: those are more recent.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:calcrun-order ── кінець секції ──
