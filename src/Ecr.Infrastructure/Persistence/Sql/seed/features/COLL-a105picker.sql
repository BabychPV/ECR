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
-- COLL:a105picker ── A1-05: вибір аркуша/таблиці/колонки в гранті без права Template.View називає причину; ru/kz — порцією COLL:a105picker у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grants.pickerForbidden', N'en', N'You cannot list templates: picking a sheet, table or column needs the Template.View permission (for example the TemplateAdministrator role). Ask a role administrator to add it to your account, then reopen the list.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a105picker ── кінець секції ──
