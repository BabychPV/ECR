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
-- PERMITS: p3-backlog ── en: PUI-4, значення списку вже прибране/змінене деінде; ru/kz — порцією PERMITS: p3-backlog нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.lists.drawer.gone', N'en', N'This value is no longer in the list: it was changed elsewhere. The list has been refreshed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: p3-backlog ── кінець секції ──
