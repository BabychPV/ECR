-- Q17: monthDataLocked ── ru/kz: Q-17/D-457, відмова DeleteRow для даних у поданих/закритих місяцях; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-ROW-0409.monthDataLocked', N'ru', N'Строку нельзя удалить: в ней есть введённые значения за месяц, который уже подан, утверждён или закрыт.'),
    (N'err.ECR-ROW-0409.monthDataLocked', N'kz', N'Бұл жолды жоюға болмайды: онда ай бойынша енгізілген мәндер бар, ал бұл ай бұрыннан тапсырылған, бекітілген немесе жабылған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- Q17: monthDataLocked ── кінець секції ──
-- Q17: monthDataLocked ── Q-17/D-457: відмова DeleteRow (409), коли в подані/закриті місяці є введені значення; ru/kz — порцією Q17 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-ROW-0409.monthDataLocked', N'en', N'This row cannot be deleted: it holds entered values for a month that is already submitted, approved or closed.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- Q17: monthDataLocked ── кінець секції ──
