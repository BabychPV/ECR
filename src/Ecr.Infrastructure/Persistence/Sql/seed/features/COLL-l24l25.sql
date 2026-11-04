-- COLL:l24l25 ── ru/kz L2-5: попередження diff публікації, коли попередня версія не обчислилась; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'publish.warning.diffPreviousNotComputed', N'ru', N'Предыдущая версия {previous} не рассчиталась на тесте {test} ({code}), поэтому в diff её выходы показаны как новые. Вердикт по новой версии не изменился.'),
    (N'publish.warning.diffPreviousNotComputed', N'kz', N'Алдыңғы {previous} нұсқасы {test} тестінде есептелмеді ({code}), сондықтан diff-те оның шығыстары жаңа ретінде көрсетілген. Жаңа нұсқа бойынша үкім өзгермеді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:l24l25 ── кінець секції ──

-- COLL:l24l25 ── L2-5: попередження diff публікації, коли попередня версія не обчислилась; ru/kz — порцією COLL:l24l25 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'publish.warning.diffPreviousNotComputed', N'en', N'Previous version {previous} did not calculate on test {test} ({code}), so the diff shows its outputs as new. The verdict on the new version is unchanged.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:l24l25 ── кінець секції ──
