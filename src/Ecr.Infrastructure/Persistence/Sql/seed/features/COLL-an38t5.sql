-- COLL:an38t5 ── ru/kz T5-04: вступ вікна відмови для неоднозначного числа; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.rejectedAmbiguousHint', N'ru', N'Из этой вставки ничего не сохранено. Число с разделителем неоднозначно: запятая может быть и десятичной, и разделителем тысяч. Запишите число без разделителей тысяч или с десятичным разделителем вашего языка.'),
    (N'grid.rejectedAmbiguousHint', N'kz', N'Осы қоюдан ештеңе сақталмады. Бөлгіші бар сан бір мағыналы емес: үтір ондық бөлгіш те, мыңдықтар бөлгіші де болуы мүмкін. Санды мыңдықтар бөлгішінсіз немесе тіліңіздің ондық бөлгішімен жазыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an38t5 ── кінець секції ──
-- COLL:an38t5 ── T5-04: вставка неоднозначного числа — власний вступ вікна відмови, не «лише для читання»; ru/kz — порцією COLL:an38t5 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.rejectedAmbiguousHint', N'en', N'Nothing from this paste was saved. In English a comma separates thousands, so a number such as 4,125 is ambiguous: enter 4125 or 4.125.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an38t5 ── кінець секції ──
