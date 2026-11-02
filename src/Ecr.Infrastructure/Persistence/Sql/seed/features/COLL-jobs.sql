-- COLL:jobs ── Завислі фонові задачі в перевірці `jobs` (аудит U16, `JobsHealthCheck`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Задачі без биття серця (покинуті процесом, що зник). Обидва — жовтий
    -- (readiness не червоніє від тла): чекають прибирання / прибирання стоїть.
    (N'health.jobs.stale',                      N'en', N'Background jobs without a heartbeat, awaiting cleanup: {count}.', 1),
    (N'health.jobs.staleUnswept',               N'en', N'Background jobs hang without a heartbeat and the cleanup does not close them: {count}.', 1),
    -- Аудит U2: `/health/ready` відповів 503 зі звітом — сторінка стану показує
    -- звіт, а над ним цю позначку, а не загальну помилку запиту.
    (N'health.notReady',                        N'en', N'The system is not ready: the readiness check failed. The component states below show what is failing.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:jobs ── кінець секції ──
