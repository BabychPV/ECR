-- LUF:fixes ── ru/kz: приймальна Land 12.10 (D4/D11); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'deny.approve.NoRight', N'ru', N'вы не можете утвердить или отклонить этот лист сейчас: нет права утверждения или очередь другой роли'),
    (N'deny.approve.NoRight', N'kz', N'қазір бұл парақты бекіте немесе қайтара алмайсыз: бекіту құқығы жоқ немесе кезек басқа рөлге тиесілі'),
    (N'grid.tree.tablesFilledSheet', N'ru', N'Заполнено таблиц на этом листе: {filled} из {total}'),
    (N'grid.tree.tablesFilledSheet', N'kz', N'Осы парақта толтырылған кестелер: {filled} / {total}'),
    (N'document.tablesFilledInDocument', N'ru', N'Полностью заполнено таблиц во всём документе: {filled} из {total}'),
    (N'document.tablesFilledInDocument', N'kz', N'Бүкіл құжатта толық толтырылған кестелер: {filled} / {total}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- LUF:fixes ── кінець секції ──
-- LUF:fixes ── приймальна Land 12.10 (D4/D11): текст відмови затвердження без права; лічильники таблиць із підписом області; ru/kz — порцією LUF:fixes нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'deny.approve.NoRight', N'en', N'you cannot approve or reject this sheet right now: you lack the approval right, or the current workflow step is for another role', 1),
    (N'grid.tree.tablesFilledSheet', N'en', N'{filled} of {total} tables filled on this sheet', 1),
    (N'document.tablesFilledInDocument', N'en', N'Tables filled completely in the whole document: {filled} of {total}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- LUF:fixes ── кінець секції ──
