-- PERMITS: a11y-dates ── ru/kz: імена кнопок місяця календаря й область прокрутки таблиці стовпців (PA2-U-02/03); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'dates.previousMonth', N'ru', N'Предыдущий месяц'),
    (N'dates.previousMonth', N'kz', N'Алдыңғы ай'),
    (N'dates.nextMonth', N'ru', N'Следующий месяц'),
    (N'dates.nextMonth', N'kz', N'Келесі ай'),
    (N'permits.editor.cols.region', N'ru', N'Таблица столбцов'),
    (N'permits.editor.cols.region', N'kz', N'Бағандар кестесі')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: a11y-dates ── кінець секції ──
-- PERMITS: a11y-dates ── en: імена кнопок «попередній/наступний місяць» календаря й область прокрутки таблиці стовпців (аудит A, PA2-U-02/03); ru/kz — порцією PERMITS: a11y-dates нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'dates.previousMonth', N'en', N'Previous month', 1),
    (N'dates.nextMonth', N'en', N'Next month', 1),
    (N'permits.editor.cols.region', N'en', N'Columns table', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: a11y-dates ── кінець секції ──
