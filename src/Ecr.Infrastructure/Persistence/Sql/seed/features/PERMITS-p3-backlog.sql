-- PERMITS: p3-backlog ── ru/kz: PUI-4, значення списку вже прибране/змінене деінде; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.lists.drawer.gone', N'ru', N'Этого значения больше нет в списке: его изменили в другом месте. Список обновлён.'),
    (N'permits.lists.drawer.gone', N'kz', N'Бұл мән тізімде енді жоқ: оны басқа жерде өзгерткен. Тізім жаңартылды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: p3-backlog ── кінець секції ──
