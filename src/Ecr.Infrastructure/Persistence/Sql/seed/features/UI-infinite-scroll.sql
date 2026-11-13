-- COLL:ui-infinite-scroll ── ru/kz: W-3 (рішення PO 11.11) — підвал переліку з нескінченною прокруткою; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'list.allShown', N'ru', N'Показаны все: {shown}'),
    (N'list.allShown', N'kz', N'Барлығы көрсетілді: {shown}'),
    (N'list.autoPaused', N'ru', N'Автозагрузка приостановлена: сузьте список фильтрами или загрузите ещё вручную.'),
    (N'list.autoPaused', N'kz', N'Автожүктеу тоқтатылды: тізімді сүзгілермен тарылтыңыз немесе қолмен тағы жүктеңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-infinite-scroll ── кінець секції ──

-- COLL:ui-infinite-scroll ── W-3: кінець видачі й пауза автодовантаження в підвалі переліку; ru/kz — порцією вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'list.allShown',   N'en', N'All {shown} shown', 1),
    (N'list.autoPaused', N'en', N'Automatic loading paused: narrow the list with filters or load more manually.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-infinite-scroll ── кінець секції ──
