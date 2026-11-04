-- COLL:p4-fanout ── Перерахунок проєкту розкладено на документні задачі (P4 ФВ-9.8, `RecalculationJob.FanOutAsync`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcFannedOut',                   N'en', N'Queued document recalculation tasks: {count}; not calculated yet.', 1),
    (N'jobs.fanOutProgress',                    N'en', N'Fanned out {total}, done {done} of {total}, errors {failed}.', 1),
    (N'jobs.fanOutPending',                     N'en', N'Fanned out, not calculated yet', 1),
    (N'jobs.fanOutDone',                        N'en', N'Done', 1),
    (N'jobs.fanOutDoneWithErrors',              N'en', N'Done with errors ({failed})', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p4-fanout ── кінець секції ──
