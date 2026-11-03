-- PERMITS: lists-edit ── ru/kz: PM-08e, запис значень списків; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.lists.editable', N'ru', N'Значение общего списка, которое ещё используют разрешения, убрать нельзя.'),
    (N'permits.lists.editable', N'kz', N'Рұқсаттар әлі қолданатын ортақ тізім мәнін алып тастауға болмайды.'),
    (N'permits.lists.drawer.note.typeEditable', N'ru', N'Эти значения видит только {owner}. Изменение сразу применяется к опубликованному типу и к его открытому черновику.'),
    (N'permits.lists.drawer.note.typeEditable', N'kz', N'Бұл мәндерді тек {owner} көреді. Өзгеріс жарияланған түрге және оның ашық жобасына бірден қолданылады.'),
    (N'permits.lists.drawer.add.label', N'ru', N'Новое значение'),
    (N'permits.lists.drawer.add.label', N'kz', N'Жаңа мән'),
    (N'permits.lists.drawer.add.submit', N'ru', N'Добавить значение'),
    (N'permits.lists.drawer.add.submit', N'kz', N'Мән қосу'),
    (N'permits.lists.drawer.edit.label', N'ru', N'Значение'),
    (N'permits.lists.drawer.edit.label', N'kz', N'Мән'),
    (N'permits.lists.drawer.edit.save', N'ru', N'Сохранить'),
    (N'permits.lists.drawer.edit.save', N'kz', N'Сақтау'),
    (N'permits.lists.drawer.edit.cancel', N'ru', N'Отмена'),
    (N'permits.lists.drawer.edit.cancel', N'kz', N'Бас тарту'),
    (N'permits.lists.drawer.rename', N'ru', N'Переименовать'),
    (N'permits.lists.drawer.rename', N'kz', N'Атын өзгерту'),
    (N'permits.lists.drawer.renameAria', N'ru', N'Переименовать {value}'),
    (N'permits.lists.drawer.renameAria', N'kz', N'{value} атын өзгерту'),
    (N'permits.lists.drawer.remove', N'ru', N'Убрать'),
    (N'permits.lists.drawer.remove', N'kz', N'Алып тастау'),
    (N'permits.lists.drawer.removeAria', N'ru', N'Убрать {value}'),
    (N'permits.lists.drawer.removeAria', N'kz', N'{value} мәнін алып тастау'),
    (N'permits.lists.drawer.added', N'ru', N'Значение добавлено'),
    (N'permits.lists.drawer.added', N'kz', N'Мән қосылды'),
    (N'permits.lists.drawer.renamed', N'ru', N'Значение переименовано'),
    (N'permits.lists.drawer.renamed', N'kz', N'Мәннің аты өзгертілді'),
    (N'permits.lists.drawer.removed', N'ru', N'Значение убрано'),
    (N'permits.lists.drawer.removed', N'kz', N'Мән алып тасталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: lists-edit ── кінець секції ──
-- PERMITS: lists-edit ── en: PM-08e, запис значень списків: шторка екрана «Lists» і «Add a value» у формі рядка дозволу; ru/kz — порцією PERMITS: lists-edit нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.lists.editable', N'en', N'A value of a shared list that permits still use cannot be removed.', 1),
    (N'permits.lists.drawer.note.typeEditable', N'en', N'Only {owner} sees these values. A change applies at once to the published type and to its open draft.', 1),
    (N'permits.lists.drawer.add.label', N'en', N'New value', 1),
    (N'permits.lists.drawer.add.submit', N'en', N'Add value', 1),
    (N'permits.lists.drawer.edit.label', N'en', N'Value', 1),
    (N'permits.lists.drawer.edit.save', N'en', N'Save', 1),
    (N'permits.lists.drawer.edit.cancel', N'en', N'Cancel', 1),
    (N'permits.lists.drawer.rename', N'en', N'Rename', 1),
    (N'permits.lists.drawer.renameAria', N'en', N'Rename {value}', 1),
    (N'permits.lists.drawer.remove', N'en', N'Remove', 1),
    (N'permits.lists.drawer.removeAria', N'en', N'Remove {value}', 1),
    (N'permits.lists.drawer.added', N'en', N'Value added', 1),
    (N'permits.lists.drawer.renamed', N'en', N'Value renamed', 1),
    (N'permits.lists.drawer.removed', N'en', N'Value removed', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: lists-edit ── кінець секції ──
