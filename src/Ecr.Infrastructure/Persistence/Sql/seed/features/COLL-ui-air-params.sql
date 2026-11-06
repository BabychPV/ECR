-- COLL:ui-air-params ── ru/kz: крок «Region and area» майстра «Import parameters…» дозволу AIR (AIR-ON-AIR); власна порція; en — порція #UiEn нижче ──
-- ⚠ kz — робочий переклад, на звірку термінологу.
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'permits.air.import.step.scope', N'ru', N'Регион и участок'),
    (N'permits.air.import.step.scope', N'kz', N'Аймақ және учаске'),
    (N'permits.air.import.hint.scope', N'ru', N'Выберите, к чему относятся параметры: регион (и участок) расположения разрешения.'),
    (N'permits.air.import.hint.scope', N'kz', N'Параметрлер неге жататынын таңдаңыз: рұқсат орналасқан аймақ (және учаске).'),
    (N'permits.air.sources.scope', N'ru', N'Регион / участок'),
    (N'permits.air.sources.scope', N'kz', N'Аймақ / учаске'),
    (N'permits.air.import.permitNumber', N'ru', N'Номер разрешения'),
    (N'permits.air.import.permitNumber', N'kz', N'Рұқсат нөмірі'),
    (N'permits.air.import.text.placeholder', N'ru', N'Введите значение'),
    (N'permits.air.import.text.placeholder', N'kz', N'Мәнді енгізіңіз'),
    (N'permits.air.import.scope.nothingFound', N'ru', N'Ничего не найдено'),
    (N'permits.air.import.scope.nothingFound', N'kz', N'Ештеңе табылмады'),
    (N'permits.air.import.scope.notInTreeTitle', N'ru', N'Региона или участка нет в дереве НАЕ'),
    (N'permits.air.import.scope.notInTreeTitle', N'kz', N'Аймақ немесе учаске НАЕ ағашында жоқ'),
    (N'permits.air.import.scope.notInTreeText', N'ru', N'Коды НАЕ из файла будут сопоставлены без учёта региона. Проверьте регион или добавьте его в дерево НАЕ.'),
    (N'permits.air.import.scope.notInTreeText', N'kz', N'Файлдағы НАЕ кодтары аймақсыз сәйкестендіріледі. Аймақты тексеріңіз немесе оны НАЕ ағашына қосыңыз.'),
    (N'permits.air.edit.button', N'ru', N'Изменить'),
    (N'permits.air.edit.button', N'kz', N'Өзгерту'),
    (N'permits.air.edit.column', N'ru', N'Действия'),
    (N'permits.air.edit.column', N'kz', N'Әрекеттер'),
    (N'permits.air.edit.source', N'ru', N'Изменить источник {nae}'),
    (N'permits.air.edit.source', N'kz', N'{nae} көзін өзгерту'),
    (N'permits.air.edit.substance', N'ru', N'Изменить вещество {code} источника {nae}'),
    (N'permits.air.edit.substance', N'kz', N'{nae} көзінің {code} затын өзгерту'),
    (N'permits.air.edit.notSaved', N'ru', N'Изменения не сохранены'),
    (N'permits.air.edit.notSaved', N'kz', N'Өзгерістер сақталмады'),
    (N'permits.air.edit.site', N'ru', N'Площадка / производство'),
    (N'permits.air.edit.site', N'kz', N'Алаң / өндіріс'),
    (N'permits.air.edit.productionCode', N'ru', N'Код производства'),
    (N'permits.air.edit.productionCode', N'kz', N'Өндіріс коды'),
    (N'permits.air.edit.x1', N'ru', N'X1'),
    (N'permits.air.edit.x1', N'kz', N'X1'),
    (N'permits.air.edit.y1', N'ru', N'Y1'),
    (N'permits.air.edit.y1', N'kz', N'Y1'),
    (N'permits.air.edit.x2', N'ru', N'X2'),
    (N'permits.air.edit.x2', N'kz', N'X2'),
    (N'permits.air.edit.y2', N'ru', N'Y2'),
    (N'permits.air.edit.y2', N'kz', N'Y2')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-params ── кінець секції ──
-- COLL:ui-air-params ── en: крок «Region and area» майстра «Import parameters…» дозволу AIR (AIR-ON-AIR, lane/integ-air-params-rc2); ru/kz — порція вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'permits.air.import.step.scope', N'en', N'Region and area', 1),
    (N'permits.air.import.hint.scope', N'en', N'Choose where the parameters belong: the region (and area) of the permit location.', 1),
    (N'permits.air.import.permitNumber', N'en', N'Permit number', 1),
    (N'permits.air.import.text.placeholder', N'en', N'Type the value', 1),
    (N'permits.air.import.scope.nothingFound', N'en', N'Nothing found', 1),
    (N'permits.air.import.scope.notInTreeTitle', N'en', N'Region or area not in the NAE tree', 1),
    (N'permits.air.sources.scope', N'en', N'Region / area', 1),
    (N'permits.air.import.scope.notInTreeText', N'en', N'NAE codes of the file will be matched without the region. Check the region or add it to the NAE tree.', 1),
    (N'permits.air.edit.button', N'en', N'Edit', 1),
    (N'permits.air.edit.column', N'en', N'Actions', 1),
    (N'permits.air.edit.source', N'en', N'Edit source {nae}', 1),
    (N'permits.air.edit.substance', N'en', N'Edit substance {code} of source {nae}', 1),
    (N'permits.air.edit.notSaved', N'en', N'Changes not saved', 1),
    (N'permits.air.edit.site', N'en', N'Site / production', 1),
    (N'permits.air.edit.productionCode', N'en', N'Production code', 1),
    (N'permits.air.edit.x1', N'en', N'X1', 1),
    (N'permits.air.edit.y1', N'en', N'Y1', 1),
    (N'permits.air.edit.x2', N'en', N'X2', 1),
    (N'permits.air.edit.y2', N'en', N'Y2', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:ui-air-params ── кінець секції ──
-- COLL:ui-air-params: змінені тексти наявних ключів (людина 02.11: таблиця параметрів — вкладка дозволу AIR, «Import parameters…»,
-- перший крок — Region і Area з довідників типу AIR; значення списку типу — по рядку або через «;»). Епілог оновлює, лише поки
-- в базі стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'permits.page.tabs.air', N'en', N'Air limits', N'Sources & parameters'),
    (N'permits.page.tabs.air', N'ru', N'Лимиты выбросов', N'Источники и параметры'),
    (N'permits.page.tabs.air', N'kz', N'Шығарынды лимиттері', N'Көздер мен параметрлер'),
    (N'permits.air.import', N'en', N'Import from Excel…', N'Import parameters…'),
    (N'permits.air.import', N'ru', N'Импорт из Excel…', N'Импорт параметров…'),
    (N'permits.air.import', N'kz', N'Excel-ден импорттау…', N'Параметрлерді импорттау…'),
    (N'permits.air.import.title', N'en', N'Import from Excel', N'Import parameters'),
    (N'permits.air.import.title', N'ru', N'Импорт из Excel', N'Импорт параметров'),
    (N'permits.air.import.title', N'kz', N'Excel-ден импорттау', N'Параметрлерді импорттау'),
    (N'permits.air.import.unsupported.title', N'en', N'Not an Air Limits permit', N'Not an AIR permit'),
    (N'permits.air.import.unsupported.title', N'ru', N'Это не разрешение «Air Limits Import»', N'Это не разрешение типа AIR'),
    (N'permits.air.import.unsupported.title', N'kz', N'Бұл «Air Limits Import» рұқсаты емес', N'Бұл AIR түріндегі рұқсат емес'),
    (N'permits.air.import.unsupported.text', N'en', N'The Excel import works only for permits of type Air Limits Import.', N'Parameters can be imported only into permits of type AIR.'),
    (N'permits.air.import.unsupported.text', N'ru', N'Импорт из Excel работает только для разрешений типа Air Limits Import.', N'Параметры можно импортировать только в разрешения типа AIR.'),
    (N'permits.air.import.unsupported.text', N'kz', N'Excel-ден импорттау тек Air Limits Import түріндегі рұқсаттарға жұмыс істейді.', N'Параметрлерді тек AIR түріндегі рұқсаттарға импорттауға болады.'),
    (N'permits.air.import.hint.file', N'en', N'Choose the workbook, the region and the area the limits belong to.', N'Choose the workbook and the import profile.'),
    (N'permits.air.import.hint.file', N'ru', N'Выберите книгу, регион и участок, к которым относятся лимиты.', N'Выберите книгу и профиль импорта.'),
    (N'permits.air.import.hint.file', N'kz', N'Лимиттер қатысты кітапты, аймақты және учаскені таңдаңыз.', N'Кітапты және импорт профилін таңдаңыз.'),
    (N'permits.air.import.region.hint', N'en', N'Regions that have NAE sources in the NAE tree.', N'Region of the permit location; the values come from the REGION column of the AIR permit type.'),
    (N'permits.air.import.region.hint', N'ru', N'Регионы, для которых в дереве НАЕ есть источники.', N'Регион расположения разрешения; значения берутся из колонки REGION типа разрешения AIR.'),
    (N'permits.air.import.region.hint', N'kz', N'НАЕ ағашында көздері бар аймақтар.', N'Рұқсат орналасқан аймақ; мәндер AIR рұқсат түрінің REGION бағанынан алынады.'),
    (N'permits.air.import.area.hint', N'en', N'Areas of the chosen region; "No area" if the region has none.', N'Area of the chosen region; shown only when the region has areas.'),
    (N'permits.air.import.area.hint', N'ru', N'Участки выбранного региона; «Без участка», если у региона их нет.', N'Участок выбранного региона; показывается, только если у региона есть участки.'),
    (N'permits.air.import.area.hint', N'kz', N'Таңдалған аймақтың учаскелері; жоқ болса «Учаскесіз».', N'Таңдалған аймақтың учаскесі; аймақта учаскелер болса ғана көрсетіледі.'),
    (N'permits.air.import.scope.empty', N'en', N'No regions in the NAE tree', N'Registry not found'),
    (N'permits.air.import.scope.empty', N'ru', N'В дереве НАЕ нет регионов', N'Справочник не найден'),
    (N'permits.air.import.scope.empty', N'kz', N'НАЕ ағашында аймақтар жоқ', N'Анықтамалық табылмады'),
    (N'permits.air.import.scope.emptyHint', N'en', N'Load or create NAE nodes first: the region and area lists come from the NAE tree.', N'The registry {registry} set for the {column} column of the AIR permit type does not exist. Create it or change the column in the permit type editor.'),
    (N'permits.air.import.scope.emptyHint', N'ru', N'Сначала загрузите или создайте узлы НАЕ: списки регионов и участков берутся из дерева НАЕ.', N'Справочник {registry}, заданный для колонки {column} типа разрешения AIR, не существует. Создайте его или измените колонку в редакторе типа разрешения.'),
    (N'permits.air.import.scope.emptyHint', N'kz', N'Алдымен НАЕ түйіндерін жүктеңіз немесе жасаңыз: аймақ пен учаске тізімі НАЕ ағашынан алынады.', N'AIR рұқсат түрінің {column} бағаны үшін берілген {registry} анықтамалығы жоқ. Оны жасаңыз немесе рұқсат түрінің редакторында бағанды өзгертіңіз.'),
    (N'permits.editor.choices.valuesHint', N'en', N'Only this type sees these values. They can also be edited on the Lists page.', N'One per line or separated by ";". Only this type sees these values. They can also be edited on the Lists page.'),
    (N'permits.editor.choices.valuesHint', N'ru', N'Эти значения видит только этот тип. Их также можно править на странице «Списки».', N'По одному в строке или через «;». Эти значения видит только этот тип. Их также можно править на странице «Списки».'),
    (N'permits.editor.choices.valuesHint', N'kz', N'Бұл мәндерді тек осы түр көреді. Оларды «Тізімдер» бетінде де өңдеуге болады.', N'Әр жолда біреуден немесе «;» арқылы. Бұл мәндерді тек осы түр көреді. Оларды «Тізімдер» бетінде де өңдеуге болады.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
