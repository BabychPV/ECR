-- COLL:periods ── Відкриття періоду з дедлайном не в майбутньому (аудит B6, `Period.Reopen`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Такий дедлайн давав «відкрито» в журналі, а період одразу був закритим.
    (N'err.ECR-PRD-0422.reopenUntilInPast',     N'en', N'The reopen deadline {until} has already passed: a period reopened until then would be closed straight away. Choose a later moment.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:periods ── кінець секції ──
