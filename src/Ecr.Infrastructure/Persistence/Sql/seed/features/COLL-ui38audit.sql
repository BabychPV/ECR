-- COLL:ui38audit ── ru/kz UI-38: журнал змін у формі макета, ім'я кнопки очищення дати; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'audit.documentCell', N'ru', N'Документ · ячейка'),
    (N'audit.documentCell', N'kz', N'Құжат · ұяшық'),
    (N'audit.wasBecomes', N'ru', N'Было → стало'),
    (N'audit.wasBecomes', N'kz', N'Болған → болды'),
    (N'audit.lateMark', N'ru', N'Запоздалая'),
    (N'audit.lateMark', N'kz', N'Кешіккен'),
    (N'audit.lateHint', N'ru', N'Внесена после срока ввода данных'),
    (N'audit.lateHint', N'kz', N'Деректерді енгізу мерзімінен кейін енгізілген'),
    (N'audit.newValue', N'ru', N'новое'),
    (N'audit.newValue', N'kz', N'жаңа'),
    (N'audit.delta', N'ru', N'Разница'),
    (N'audit.delta', N'kz', N'Айырма'),
    (N'audit.originLabel.UserEdit', N'ru', N'Введено пользователем'),
    (N'audit.originLabel.UserEdit', N'kz', N'Пайдаланушы енгізген'),
    (N'audit.originLabel.Import', N'ru', N'Импорт из Excel'),
    (N'audit.originLabel.Import', N'kz', N'Excel-ден импорт'),
    (N'audit.originLabel.Recalculation', N'ru', N'Пересчёт'),
    (N'audit.originLabel.Recalculation', N'kz', N'Қайта есептеу'),
    (N'audit.originLabel.Migration', N'ru', N'Миграция'),
    (N'audit.originLabel.Migration', N'kz', N'Көшіру'),
    (N'dates.clear', N'ru', N'Очистить дату'),
    (N'dates.clear', N'kz', N'Күнді тазалау'),
    (N'dates.clearNamed', N'ru', N'Очистить: {field}'),
    (N'dates.clearNamed', N'kz', N'Тазалау: {field}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui38audit ── кінець секції ──
