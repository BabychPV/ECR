-- COLL:coverage-refusal ── Джерело відмовило віддати дані інтервалу з кодом каталогу (HSE301 A7/F4e, `CollectionRunner`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- Подія журналу покриття `SourceDataRefused`: інтервал лишається прогалиною, наздоганяння повторить його.
    -- `{message}` — вкладена причина `jobs.collectionRunReason` («код: ключ адаптера з параметрами»).
    (N'status.coverage.SourceDataRefused',      N'en', N'Source refused the data', 1),
    (N'coverageEvents.sourceDataRefused',       N'en', N'Attribute "{path}", interval [{from}, {to}) was not collected: {message}. The interval stays a gap; catch-up retries it on every run until the source data is fixed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:coverage-refusal ── кінець секції ──
