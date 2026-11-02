-- JOBL ── ru/kz назв видів задач (jobLabel.ts, KindKeys); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'jobs.kind.sourceEventSync', N'ru', N'Синхронизация событий источника'),
    (N'jobs.kind.sourceEventSync', N'kz', N'Көз оқиғаларын синхрондау'),
    (N'jobs.kind.consistencyCheck', N'ru', N'Проверка согласованности'),
    (N'jobs.kind.consistencyCheck', N'kz', N'Келісімділікті тексеру'),
    (N'jobs.kind.orphanScan', N'ru', N'Поиск осиротевших данных'),
    (N'jobs.kind.orphanScan', N'kz', N'Иесіз деректерді іздеу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- JOBL ── кінець секції ──

