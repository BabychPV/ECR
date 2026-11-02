-- DOC:an37 ── ru/kz відмови видалення документа з мапою подій джерела (L10-06); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-DOC-0409.deleteHasEventMap', N'ru', N'Документ нельзя удалить: в него пишет сопоставление событий источника. Сначала удалите сопоставление.'),
    (N'err.ECR-DOC-0409.deleteHasEventMap', N'kz', N'Құжатты жою мүмкін емес: оған дереккөз оқиғаларының сәйкестендіруі жазады. Алдымен сәйкестендіруді жойыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOC:an37 ── кінець секції ──
