-- COLL:collector-msg ── Прогрес і причини прогону збору конвертом (аудит U12, `Q-326`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- `CollectionRunner` писав прогрес (`itg.JobProgress.Message`) і причину
    -- (`itg.CollectionRun.ErrorMessage`) готовими українськими реченнями. Тепер —
    -- конверт `JobProgressMessageEnvelope`; прогрес резолвить `GetJobStatusHandler`,
    -- причину — шухляда прогону (`collectionRunMessage.ts`) і зведення `NotificationJob`.
    -- `{message}` — вкладена причина (`Inner`), `{detail}`/`{error}` — текст джерела чи винятку як є.
    (N'jobs.collectionProgress',                  N'en', N'Collected points: {points}; intervals {step} of {total}', 1),
    (N'jobs.collectionDone',                      N'en', N'Collection finished: {points} points', 1),
    (N'jobs.collectionDonePartial',               N'en', N'Collection partly finished: {points} points; the rest of the range is queued for catch-up', 1),
    (N'jobs.collectionRunReason',                 N'en', N'{code}: {message}', 1),
    (N'jobs.collectionSourceUnavailable',         N'en', N'the source is unavailable', 1),
    (N'jobs.collectionSourceError',               N'en', N'the source refused: {detail}', 1),
    (N'jobs.collectionSameTimestamp',             N'en', N'attribute "{path}": more than {limit} points share the timestamp {cursor}; paging by time cannot advance, [{cursor}, {to}) is not read.', 1),
    (N'jobs.collectionPageLimit',                 N'en', N'attribute "{path}": read {pages} pages of {limit} points up to {cursor}, the page limit per run. Coverage is recorded for what was read; catch-up reads [{cursor}, {to}) from this point.', 1),
    (N'jobs.collectionUnitChanged',               N'en', N'attribute "{path}" returns unit "{actual}", but the mapping declares unit {declared}. The mapping is paused.', 1),
    (N'jobs.collectionTimeout',                   N'en', N'the run exceeded the time limit of {minutes} min: the source responds, but too slowly. The unread part goes to catch-up next time.', 1),
    (N'jobs.collectionCancelled',                 N'en', N'the run was cancelled externally (the job was stopped). The unread part goes to catch-up next time.', 1),
    (N'jobs.collectionRuleFailed',                N'en', N'{code}: the collection run was stopped by a rule. {error}', 1),
    (N'jobs.collectionRunFailed',                 N'en', N'The collection run failed. {error}', 1),
    (N'jobs.collectionCloseFailed',               N'en', N'The collection run could not be closed. {error}', 1),
    -- `CollectionFailure.AuthenticationRefusedReason` (H-20): ключ розпізнає зведення
    -- (`IsAuthenticationRefusal`); `{detail}` — текст відмови адаптера, може бути порожнім.
    (N'jobs.collectionAuthRefused',               N'en', N'Source {sourceCode} refused authentication: repeating the request with the same credentials changes nothing, an administrator must intervene. {detail}', 1),
    -- `AbandonedWorkSweeper.AbandonedCollectionRunReason`: прогін, який ніхто не закрив.
    (N'jobs.collectionAbandoned',                 N'en', N'The run was not closed: the job was cancelled or the process stopped before the run recorded its result.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:collector-msg ── кінець секції ──
