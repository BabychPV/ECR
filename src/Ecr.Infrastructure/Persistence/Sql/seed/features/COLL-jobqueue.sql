-- COLL:jobqueue ── Черга задач у базі (MI-02, D-208, `DbJobQueue`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Ретрай задачі поглинула Queued позаду на ту саму ціль (вона вже несе актуальний стан цілі).
    (N'jobs.absorbedBy',                        N'en', N'Superseded by job {jobId}, queued for the same target.', 1),
    -- Отруйна задача: оренду втрачено {reclaims} разів поспіль — процес-виконавець щоразу зникав.
    (N'jobs.leaseLostTooOften',                 N'en', N'Stopped: the worker lost this job {reclaims} times in a row (its process stopped each time). Restart the job after checking the logs.', 1),
    -- Задача перевищила Jobs:Workers:MaxDuration (I1, `JobWorker`): скасовано, без ретраю.
    (N'jobs.maxDurationExceeded',               N'en', N'Stopped: the job ran longer than the limit of {limit}. Check the logs, then restart the job or raise the limit.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:jobqueue ── кінець секції ──
