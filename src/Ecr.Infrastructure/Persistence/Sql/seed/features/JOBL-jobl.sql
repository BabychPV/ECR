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

-- JOBL ── людські назви видів фонових задач, яких бракувало в jobLabel.ts (KindKeys) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'jobs.kind.sourceEventSync',  N'en', N'Source event sync', 1),
    (N'jobs.kind.consistencyCheck', N'en', N'Consistency check', 1),
    (N'jobs.kind.orphanScan',       N'en', N'Orphaned data scan', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- JOBL ── кінець секції ──
