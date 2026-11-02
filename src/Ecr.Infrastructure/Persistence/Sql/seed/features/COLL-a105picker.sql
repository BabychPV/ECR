-- COLL:a105picker ── ru/kz A1-05: причина порожнього вибору аркуша/таблиці/колонки в гранті; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grants.pickerForbidden', N'ru', N'Список шаблонов вам недоступен: для выбора листа, таблицы или столбца нужно право Template.View (например, роль TemplateAdministrator). Попросите администратора ролей добавить его вашей учётной записи и откройте список снова.'),
    (N'grants.pickerForbidden', N'kz', N'Үлгілер тізімі Сізге қолжетімсіз: парақты, кестені немесе бағанды таңдау үшін Template.View құқығы қажет (мысалы, TemplateAdministrator рөлі). Рөл әкімшісінен оны есептік жазбаңызға қосуын сұраңыз да, тізімді қайта ашыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a105picker ── кінець секції ──
