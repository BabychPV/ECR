-- COLL:an28 ── ru/kz AN-28/L8-01; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.unsavedBlocksAction', N'ru', N'Несохранённые изменения в таблице не удалось сохранить, поэтому действие не выполнено. Исправьте ошибки в ячейках или повторите попытку.'),
    (N'document.unsavedBlocksAction', N'kz', N'Кестедегі сақталмаған өзгерістерді сақтау мүмкін болмады, сондықтан әрекет орындалмады. Ұяшықтардағы қателерді түзетіңіз немесе әрекетті қайталаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an28 ── кінець секції ──

-- COLL:an28 ── AN-28/L8-01: дія над документом не виконується, якщо набране в сітці не збереглося; ru/kz — порцією COLL:an28 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.unsavedBlocksAction', N'en', N'Unsaved changes in the grid could not be saved, so the action was not performed. Fix the cell errors or try again.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an28 ── кінець секції ──
