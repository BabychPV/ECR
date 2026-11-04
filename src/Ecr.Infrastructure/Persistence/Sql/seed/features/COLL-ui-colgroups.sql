-- COLL:ui-colgroups ── ru/kz: групові заголовки колонок (конструктор шаблону); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'colgroups.manage', N'ru', N'Группы колонок…'),
    (N'colgroups.manage', N'kz', N'Бағандар топтары…'),
    (N'colgroups.title', N'ru', N'Группы колонок'),
    (N'colgroups.title', N'kz', N'Бағандар топтары'),
    (N'colgroups.hint', N'ru', N'Объединяйте соседние колонки под общим заголовком, до 3 уровней. Колонки одной группы должны идти подряд. При удалении группы её колонки освобождаются.'),
    (N'colgroups.hint', N'kz', N'Көрші бағандарды ортақ тақырыптың астына біріктіріңіз, 3 деңгейге дейін. Бір топтың бағандары қатар тұруы керек. Топты жойғанда оның бағандары босатылады.'),
    (N'colgroups.add', N'ru', N'Добавить группу'),
    (N'colgroups.add', N'kz', N'Топ қосу'),
    (N'colgroups.addChild', N'ru', N'Добавить подгруппу'),
    (N'colgroups.addChild', N'kz', N'Ішкі топ қосу'),
    (N'colgroups.remove', N'ru', N'Удалить группу'),
    (N'colgroups.remove', N'kz', N'Топты жою'),
    (N'colgroups.moveUp', N'ru', N'Выше'),
    (N'colgroups.moveUp', N'kz', N'Жоғары'),
    (N'colgroups.moveDown', N'ru', N'Ниже'),
    (N'colgroups.moveDown', N'kz', N'Төмен'),
    (N'colgroups.label', N'ru', N'Название группы'),
    (N'colgroups.label', N'kz', N'Топ атауы'),
    (N'colgroups.empty', N'ru', N'Групп пока нет.'),
    (N'colgroups.empty', N'kz', N'Топтар әзірге жоқ.'),
    (N'colgroups.maxDepth', N'ru', N'В дереве групп не больше 3 уровней.'),
    (N'colgroups.maxDepth', N'kz', N'Топ ағашында 3 деңгейден артық болмайды.'),
    (N'colgroups.needName', N'ru', N'У каждой группы должно быть название хотя бы на одном языке.'),
    (N'colgroups.needName', N'kz', N'Әр топтың кемінде бір тілде атауы болуы керек.'),
    (N'colgroups.save', N'ru', N'Сохранить группы'),
    (N'colgroups.save', N'kz', N'Топтарды сақтау'),
    (N'colgroups.saved', N'ru', N'Группы колонок сохранены (ревизия {revision}).'),
    (N'colgroups.saved', N'kz', N'Бағандар топтары сақталды (ревизия {revision}).'),
    (N'colgroups.field', N'ru', N'Группа заголовка'),
    (N'colgroups.field', N'kz', N'Тақырып тобы'),
    (N'colgroups.fieldHint', N'ru', N'Колонки одной группы показываются под общим заголовком. В одной группе могут быть только соседние колонки.'),
    (N'colgroups.fieldHint', N'kz', N'Бір топтың бағандары ортақ тақырыптың астында көрсетіледі. Бір топқа тек көрші бағандар кіре алады.'),
    (N'colgroups.none', N'ru', N'Без группы'),
    (N'colgroups.none', N'kz', N'Топсыз')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-colgroups ── кінець секції ──
-- COLL:ui-colgroups ── групові заголовки колонок (до 3 рівнів): конструктор шаблону; ru/kz — порцією COLL:ui-colgroups нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'colgroups.manage', N'en', N'Column groups…', 1),
    (N'colgroups.title', N'en', N'Column groups', 1),
    (N'colgroups.hint', N'en', N'Put neighbouring columns under a shared header, up to 3 levels. Columns of one group must be adjacent. Removing a group releases its columns.', 1),
    (N'colgroups.add', N'en', N'Add group', 1),
    (N'colgroups.addChild', N'en', N'Add subgroup', 1),
    (N'colgroups.remove', N'en', N'Remove group', 1),
    (N'colgroups.moveUp', N'en', N'Move up', 1),
    (N'colgroups.moveDown', N'en', N'Move down', 1),
    (N'colgroups.label', N'en', N'Group name', 1),
    (N'colgroups.empty', N'en', N'No groups yet.', 1),
    (N'colgroups.maxDepth', N'en', N'A group tree has at most 3 levels.', 1),
    (N'colgroups.needName', N'en', N'Every group needs a name in at least one language.', 1),
    (N'colgroups.save', N'en', N'Save groups', 1),
    (N'colgroups.saved', N'en', N'Column groups saved (revision {revision}).', 1),
    (N'colgroups.field', N'en', N'Header group', 1),
    (N'colgroups.fieldHint', N'en', N'Columns that share a group are shown under one header. Only neighbouring columns can share a group.', 1),
    (N'colgroups.none', N'en', N'No group', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-colgroups ── кінець секції ──
