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

