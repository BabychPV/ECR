-- D446:stale-undetermined ── ru/kz: позначка «актуальність результатів не визначається» (D-446); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.staleUndetermined', N'ru', N'Актуальность не определена'),
    (N'documents.staleUndetermined', N'kz', N'Өзектілігі анықталмайды'),
    (N'documents.staleUndeterminedHint', N'ru', N'Часть данных вам не открыта — актуальность результатов не определяется'),
    (N'documents.staleUndeterminedHint', N'kz', N'Деректердің бір бөлігі сізге ашық емес — нәтижелердің өзектілігі анықталмайды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D446:stale-undetermined ── кінець секції ──
