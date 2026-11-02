-- COLL:r7y8-snapshot-submitted ── ru/kz: R7-Y8 / Y8-01: побудова над поданим зрізом — ECR-RPT-0409, тост із шляхом; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-RPT-0409.periodSubmittedRebuild', N'ru', N'Срез {snapshotId} этого периода подан, поэтому новый срез не строится. Сначала верните поданные листы на правки (действие листа «Переоткрыть для правок»), затем постройте снова.'),
    (N'err.ECR-RPT-0409.periodSubmittedRebuild', N'kz', N'Осы кезеңнің {snapshotId} кесіндісі тапсырылған, сондықтан жаңа кесінді құрылмайды. Алдымен тапсырылған парақтарды түзетуге қайтарыңыз (парақтың «Түзету үшін қайта ашу» әрекеті), содан кейін қайта құрыңыз.'),
    (N'snapshots.buildRefusedSubmitted', N'ru', N'Срез этого периода подан, поэтому новый не строится. Сначала верните поданные листы на правки (действие листа «Переоткрыть для правок»), затем постройте снова.'),
    (N'snapshots.buildRefusedSubmitted', N'kz', N'Осы кезеңнің кесіндісі тапсырылған, сондықтан жаңасы құрылмайды. Алдымен тапсырылған парақтарды түзетуге қайтарыңыз (парақтың «Түзету үшін қайта ашу» әрекеті), содан кейін қайта құрыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r7y8-snapshot-submitted ── кінець секції ──

-- COLL:r7y8-snapshot-submitted ── R7-Y8 / Y8-01: побудова над поданим зрізом — ECR-RPT-0409 без повторів задачі, тост із шляхом; ru/kz — порцією COLL:r7y8-snapshot-submitted нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-RPT-0409.periodSubmittedRebuild', N'en', N'Snapshot {snapshotId} of this period was submitted, so a new snapshot is not built. Return the submitted sheets for edits first (the sheet''s «Return for edits» action), then build again.', 1),
    (N'snapshots.buildRefusedSubmitted', N'en', N'This period''s snapshot was submitted, so a new one is not built. Return the submitted sheets for edits first (the sheet''s «Return for edits» action), then build again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:r7y8-snapshot-submitted ── кінець секції ──
