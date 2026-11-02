-- COLL:schedule ── Вікно збору (ФВ-13.15) і заборона розкладу власних форм (ФВ-12.8) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- ⚠ Межі 1–366 днів — `CollectionSchedule.Min/MaxLookbackDays`; у ТЗ їх немає.
    (N'schedule.lookbackDays',           N'en', N'Collection window (days)', 1),
    (N'schedule.lookbackHint',           N'en', N'Each run re-reads this many days back, so a missed run is covered by the next one.', 1),
    (N'schedule.lookbackRange',          N'en', N'Enter a whole number of days from {min} to {max}.', 1),
    (N'err.ECR-REQ-0422.collectionScheduleLookback', N'en', N'The collection window must be between {min} and {max} days.', 1),
    (N'err.ECR-REQ-0422.scheduleForLocalEntity',     N'en', N'"{code}" is an ECR form: its data arrives when it is entered, so it cannot have a collection schedule.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:schedule ── кінець секції ──
