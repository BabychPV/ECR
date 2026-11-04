-- COLL:an39b ── ru/kz хвоста сітки; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.heldEditBlocksAction', N'ru', N'Изменение в подсвеченной ячейке отклонено, поэтому действие не выполнено. Исправьте или отмените его и повторите.'),
    (N'document.heldEditBlocksAction', N'kz', N'Белгіленген ұяшықтағы өзгеріс қабылданбады, сондықтан әрекет орындалмады. Оны түзетіңіз немесе болдырмаңыз да, қайталап көріңіз.'),
    (N'document.unsavedNotIncluded', N'ru', N'Часть изменений в таблице не сохранена и не учтена: действие выполнено по последним сохранённым значениям.'),
    (N'document.unsavedNotIncluded', N'kz', N'Кестедегі кейбір өзгерістер сақталмаған және ескерілмеді: әрекет соңғы сақталған мәндер бойынша орындалды.'),
    (N'app.languageAfterSave', N'ru', N'Страница пока на прежнем языке: несохранённые изменения были бы потеряны. Сохраните или отмените их и нажмите «Переключить сейчас».'),
    (N'app.languageAfterSave', N'kz', N'Бет әзірге бұрынғы тілде: сақталмаған өзгерістер жоғалар еді. Оларды сақтаңыз немесе болдырмаңыз да, «Қазір ауыстыру» түймесін басыңыз.'),
    (N'app.languageSwitchNow', N'ru', N'Переключить сейчас'),
    (N'app.languageSwitchNow', N'kz', N'Қазір ауыстыру')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an39b ── кінець секції ──
-- COLL:an39b ── хвіст сітки після AN-39 (AN-28 P2-1/P2-2, L8-11/17/20); ru/kz — власна порція нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.heldEditBlocksAction', N'en', N'A change in the highlighted cell was rejected, so the action was not performed. Fix or undo it and try again.', 1),
    (N'document.unsavedNotIncluded', N'en', N'Some changes in the grid are not saved and are not included: the action uses the last saved values.', 1),
    (N'app.languageAfterSave', N'en', N'The page is still in the previous language: unsaved changes would be lost. Save or undo them, then press Switch now.', 1),
    (N'app.languageSwitchNow', N'en', N'Switch now', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an39b ── кінець секції ──
