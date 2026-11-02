-- PERMITS: add-value ── ru/kz: PM-08e, «Not in the list? Add a value…» у формі рядка дозволу; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.page.row.addValue', N'ru', N'Нет в списке? Добавить значение…'),
    (N'permits.page.row.addValue', N'kz', N'Тізімде жоқ па? Мән қосу…'),
    (N'permits.page.addValue.title', N'ru', N'Добавить значение'),
    (N'permits.page.addValue.title', N'kz', N'Мән қосу'),
    (N'permits.page.addValue.sharedNote', N'ru', N'Значение добавляется в общий список {name} и предлагается каждому типу разрешения, который его использует.'),
    (N'permits.page.addValue.sharedNote', N'kz', N'Мән {name} ортақ тізіміне қосылады және оны қолданатын әрбір рұқсат түріне ұсынылады.'),
    (N'permits.page.addValue.typeNote', N'ru', N'Значение добавляется только в список этого типа разрешения.'),
    (N'permits.page.addValue.typeNote', N'kz', N'Мән тек осы рұқсат түрінің тізіміне қосылады.'),
    (N'permits.page.addValue.label', N'ru', N'Значение'),
    (N'permits.page.addValue.label', N'kz', N'Мән'),
    (N'permits.page.addValue.submit', N'ru', N'Добавить значение'),
    (N'permits.page.addValue.submit', N'kz', N'Мән қосу'),
    (N'permits.page.addValue.cancel', N'ru', N'Отмена'),
    (N'permits.page.addValue.cancel', N'kz', N'Бас тарту'),
    (N'permits.page.addValue.added', N'ru', N'Значение добавлено'),
    (N'permits.page.addValue.added', N'kz', N'Мән қосылды'),
    (N'permits.page.addValue.close', N'ru', N'Закрыть'),
    (N'permits.page.addValue.close', N'kz', N'Жабу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: add-value ── кінець секції ──
