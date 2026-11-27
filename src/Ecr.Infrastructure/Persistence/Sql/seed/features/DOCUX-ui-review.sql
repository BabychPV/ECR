-- DOCUX:ui-review ── ru/kz: огляд UI всіх сторінок меню (координатор 27.11, lane/integ-docux-polish); власна порція ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'filters.all', N'ru', N'Все'),
    (N'filters.all', N'kz', N'Барлығы'),
    (N'templates.stateAll', N'ru', N'Все состояния'),
    (N'templates.stateAll', N'kz', N'Барлық күйлер'),
    (N'permits.list.filter.typeAll', N'ru', N'Все типы'),
    (N'permits.list.filter.typeAll', N'kz', N'Барлық түрлер'),
    (N'permits.list.filter.stateAll', N'ru', N'Все состояния'),
    (N'permits.list.filter.stateAll', N'kz', N'Барлық күйлер'),
    (N'permits.types.filter.categoryAll', N'ru', N'Все категории'),
    (N'permits.types.filter.categoryAll', N'kz', N'Барлық санаттар'),
    (N'units.dimensionAll', N'ru', N'Все размерности'),
    (N'units.dimensionAll', N'kz', N'Барлық өлшемдер')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- DOCUX:ui-review ── кінець секції ──

-- DOCUX:ui-review ── en; ru/kz — порцією DOCUX:ui-review вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'filters.all', N'en', N'All', 1),
    (N'templates.stateAll', N'en', N'All states', 1),
    (N'permits.list.filter.typeAll', N'en', N'All types', 1),
    (N'permits.list.filter.stateAll', N'en', N'All states', 1),
    (N'permits.types.filter.categoryAll', N'en', N'All categories', 1),
    (N'units.dimensionAll', N'en', N'All dimensions', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- DOCUX:ui-review ── кінець секції ──
