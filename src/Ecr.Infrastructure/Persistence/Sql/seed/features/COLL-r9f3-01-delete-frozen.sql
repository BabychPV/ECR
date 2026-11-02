-- COLL:r9f3-01-delete-frozen ── ru/kz: R9-F3 / F3-01: видалення документа відмовляє в архівованому проєкті й за даних у закритому періоді; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-0409.deleteProjectArchived', N'ru', N'Документ нельзя удалить: его проект в архиве.'),
    (N'err.ECR-DOC-0409.deleteProjectArchived', N'kz', N'Құжатты жою мүмкін емес: оның жобасы мұрағатта.'),
    (N'err.ECR-DOC-0409.deleteClosedPeriod', N'ru', N'Документ нельзя удалить: в нём есть данные закрытого периода {periodKey}. Сначала переоткройте период.'),
    (N'err.ECR-DOC-0409.deleteClosedPeriod', N'kz', N'Құжатты жою мүмкін емес: онда жабық {periodKey} кезеңінің деректері бар. Алдымен кезеңді қайта ашыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:r9f3-01-delete-frozen ── кінець секції ──
