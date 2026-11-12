-- D446:stale-undetermined ── ru/kz: позначка «актуальність результатів не визначається» (D-446); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'documents.staleUndetermined', N'ru', N'Статус результатов скрыт'),
    (N'documents.staleUndetermined', N'kz', N'Нәтижелер күйі жасырын'),
    (N'documents.staleUndeterminedHint', N'ru', N'Часть документа вам не открыта, поэтому нельзя показать, актуальны ли рассчитанные результаты. Проверить это и при необходимости пересчитать может тот, кому открыт весь документ.'),
    (N'documents.staleUndeterminedHint', N'kz', N'Құжаттың бір бөлігі сізге ашық емес, сондықтан есептелген нәтижелердің өзектілігін көрсету мүмкін емес. Мұны бүкіл құжатқа қолжетімділігі бар адам тексеріп, қажет болса қайта есептей алады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- D446:stale-undetermined ── кінець секції ──
-- D446:stale-undetermined ── en: позначка «актуальність результатів не визначається» — частина документа читачеві закрита (D-446, `resultsStaleUndetermined`); ru/kz — порцією D446:stale-undetermined нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'documents.staleUndetermined', N'en', N'Results status hidden', 1),
    (N'documents.staleUndeterminedHint', N'en', N'Part of this document is hidden from you, so we can''t show whether its calculated results are up to date. Someone who can see the whole document can check this and recalculate if needed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- D446:stale-undetermined ── кінець секції ──
