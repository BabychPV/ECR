-- HSE301:a5b ── прогрес синку подій джерела в рядки таблиць (SourceEventSyncJob) ───────────
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.sourceEventsNoMaps', N'en', N'No active event mappings', 1),
    (N'jobs.sourceEventsReading', N'en', N'Reading events from the source', 1),
    (N'jobs.sourceEventsWriting', N'en', N'Writing event rows', 1),
    (N'jobs.sourceEventsDone', N'en', N'Created {created}, updated {updated}, kept manual {keptManual}, missing {missing}, open {open}, unmapped {unmapped}, period closed {closed}, pending {pending}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:a5b ── кінець секції ───────────────────────────────────────────────────────────
