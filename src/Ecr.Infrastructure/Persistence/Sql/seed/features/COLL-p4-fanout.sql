-- COLL:p4-fanout ── Перерахунок проєкту розкладено на документні задачі (P4 ФВ-9.8, `RecalculationJob.FanOutAsync`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.recalcFannedOut',                   N'en', N'Queued document recalculation tasks: {count}; not calculated yet.', 1),
    (N'jobs.fanOutProgress',                    N'en', N'Fanned out {total}, done {done} of {total}, errors {failed}.', 1),
    (N'jobs.fanOutPending',                     N'en', N'Fanned out, not calculated yet', 1),
    (N'jobs.fanOutDone',                        N'en', N'Done', 1),
    (N'jobs.fanOutDoneWithErrors',              N'en', N'Done with errors ({failed})', 1),
    -- ✎ PSS-15 (D-562): підсумок нічного розкладу проєкту (`RecalculationJob.NightlyFannedOutKey`).
    (N'jobs.recalcNightlyFannedOut',            N'en', N'Nightly recalculation for {year}: queued {count} documents; skipped {skipped} (inputs unchanged or no open period this year).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p4-fanout ── кінець секції ──

-- COLL:p4-fanout ── ru/kz: PSS-15, підсумок нічного розкладу ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.recalcNightlyFannedOut', N'ru', N'Ночной пересчёт за {year}: поставлено документов {count}; пропущено {skipped} (входы не менялись или в этом году нет открытого периода).'),
    (N'jobs.recalcNightlyFannedOut', N'kz', N'{year} жылғы түнгі қайта есептеу: кезекке {count} құжат қойылды; {skipped} өткізілді (кірістер өзгермеген немесе осы жылы ашық кезең жоқ).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p4-fanout ── кінець секції ──
