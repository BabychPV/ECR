-- D16:recalc-budget ── Перерахунок вийшов за бюджет ПРД-13 (`RecalculationBudgetMonitor`, `JobsHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Конверт у `itg.JobProgress.Message` успішної задачі: `{seconds}` — тривалість, `{limit}` — поріг `Calculations:FullYearWarnSeconds`.
    (N'jobs.recalcOverBudget',                  N'en', N'Recalculation finished in {seconds} s, over the {limit} s budget. Check the database load and the recalculation worker pool.', 1),
    (N'health.jobs.recalcOverBudget',           N'en', N'Recalculation jobs that ran over the time budget in the last 24 hours: {count}. See the job list for the duration.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D16:recalc-budget ── кінець секції ──
