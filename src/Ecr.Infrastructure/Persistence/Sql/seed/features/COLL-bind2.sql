-- COLL:bind2 ── ru/kz P2-1: дві активні прив'язки з однаковим предикатом на одну колонку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.bindingColumnConflict', N'ru', N'У столбца {columnCode} несколько активных привязок выходов с одним и тем же условием строк ({outputCode}): на каждую строку претендовали бы две привязки. Сузьте условие или отключите одну из привязок.'),
    (N'err.ECR-TMPL-0422.bindingColumnConflict', N'kz', N'{columnCode} бағанында жол шарты бірдей бірнеше белсенді шығыс байланыстыруы бар ({outputCode}): әр жолға екі байланыстыру таласар еді. Шартты тарылтыңыз немесе байланыстырулардың бірін өшіріңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:bind2 ── кінець секції ──

