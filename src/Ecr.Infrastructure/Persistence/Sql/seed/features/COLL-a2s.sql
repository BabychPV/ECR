-- COLL:a2s ── ru/kz A2-03: підказка для Check без ключів; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'validation.check.noKeys', N'ru', N'Сверка без ключей сравнивает только при ровно одной строке таблицы-приёмника; в ней строк: {targetRows}, поэтому ничего не сравнивалось. Неизвестные поля сопоставления строк игнорируются.'),
    (N'validation.check.noKeys', N'kz', N'Кілтсіз салыстыру қабылдағыш кестеде дәл бір жол болғанда ғана жұмыс істейді; онда {targetRows} жол бар, сондықтан ештеңе салыстырылмады. Жолдарды сәйкестендірудің белгісіз өрістері еленбейді.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a2s ── кінець секції ──
-- COLL:a2s ── A2-03: підказка (Info) для Check без ключів; ru/kz — порцією COLL:a2s у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'validation.check.noKeys', N'en', N'A Check without keys compares only when the receiving table has exactly one row; it has {targetRows}, so nothing was compared. Unknown row matching fields are ignored.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a2s ── кінець секції ──
