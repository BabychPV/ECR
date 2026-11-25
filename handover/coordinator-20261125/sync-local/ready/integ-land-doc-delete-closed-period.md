# Картка: land-doc-delete-closed-period Річні атрибути рядка Land у 202601 блокують видалення документа
Статус: planned
Гілка: lane/integ-land-doc-delete-closed-period
Пачка: b13
Ризик: R3
Рішення: UI-тест doc 100 25.11: Delete → ECR-DOC-0409 «has data in the closed period 202601», заголовок «The document is submitted» хибний
Дизайн: не потрібен

## 2. Критерії приймання
| AC | Що спостерігаємо (вхід → результат) | Доказ | SHA |
|----|----|----|----|
| AC-0 | заголовок помилки відповідає причині (закритий період, не «submitted») | vitest + тест API | |
| AC-1 | рішення щодо річних атрибутів (Status/Type/Power у 202601) і видалення чернетки — дизайн + квіз людині | дизайн | |
