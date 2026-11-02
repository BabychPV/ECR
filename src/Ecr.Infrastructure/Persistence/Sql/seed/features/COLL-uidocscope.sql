-- COLL:uidocscope ── ru/kz P1 прихований аркуш: видалення перевіряє сервер; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.deleteServerChecks', N'ru', N'Сервер проверяет все листы документа, включая недоступные вам, и откажет, если хотя бы один из них не черновик.'),
    (N'documents.deleteServerChecks', N'kz', N'Сервер құжаттың барлық парақтарын, соның ішінде сізге көрінбейтіндерін де тексереді және кемінде біреуі жоба болмаса, бас тартады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uidocscope ── кінець секції ──
