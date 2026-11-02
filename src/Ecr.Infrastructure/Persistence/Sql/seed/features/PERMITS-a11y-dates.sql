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
