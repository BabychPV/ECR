-- CONSTRUCTOR:preview ── ru/kz попереднього перегляду таблиці шаблону (ФВ-2.6); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'tablePreview.open', N'ru', N'Предпросмотр'),
    (N'tablePreview.open', N'kz', N'Алдын ала қарау'),
    (N'tablePreview.title', N'ru', N'Предпросмотр таблицы: {name}'),
    (N'tablePreview.title', N'kz', N'Кестені алдын ала қарау: {name}'),
    (N'tablePreview.hint', N'ru', N'Как таблица будет выглядеть в документе: текущий порядок колонок и строк, типы данных, единицы и правила условного форматирования. Здесь ничего не сохраняется.'),
    (N'tablePreview.hint', N'kz', N'Кесте құжатта қалай көрінеді: бағандар мен жолдардың ағымдағы реті, деректер түрлері, өлшем бірліктері және шартты пішімдеу ережелері. Мұнда ештеңе сақталмайды.'),
    (N'tablePreview.sample', N'ru', N'Пример значения'),
    (N'tablePreview.sample', N'kz', N'Мән үлгісі'),
    (N'tablePreview.sampleHint', N'ru', N'Каждая ячейка показывает это значение и оформляется первым сработавшим правилом своего столбца.'),
    (N'tablePreview.sampleHint', N'kz', N'Әр ұяшық осы мәнді көрсетеді және өз бағанының алғашқы сәйкес келген ережесімен пішімделеді.'),
    (N'tablePreview.tableLabel', N'ru', N'Предпросмотр таблицы'),
    (N'tablePreview.tableLabel', N'kz', N'Кестені алдын ала қарау'),
    (N'tablePreview.row', N'ru', N'Строка'),
    (N'tablePreview.row', N'kz', N'Жол'),
    (N'tablePreview.required', N'ru', N'обязательная'),
    (N'tablePreview.required', N'kz', N'міндетті'),
    (N'tablePreview.readOnly', N'ru', N'только чтение'),
    (N'tablePreview.readOnly', N'kz', N'тек оқу'),
    (N'tablePreview.noRules', N'ru', N'Нет правил'),
    (N'tablePreview.noRules', N'kz', N'Ережелер жоқ'),
    (N'tablePreview.newRow', N'ru', N'Новая строка'),
    (N'tablePreview.newRow', N'kz', N'Жаңа жол'),
    (N'tablePreview.noColumns', N'ru', N'В таблице пока нет видимых колонок.'),
    (N'tablePreview.noColumns', N'kz', N'Кестеде әзірге көрінетін бағандар жоқ.'),
    (N'tablePreview.noRows', N'ru', N'В таблице нет заданных строк: строки добавляются в документе.'),
    (N'tablePreview.noRows', N'kz', N'Кестеде алдын ала берілген жолдар жоқ: жолдар құжатта қосылады.'),
    (N'tablePreview.hiddenColumns', N'ru', N'Скрытые столбцы не показаны: {count}'),
    (N'tablePreview.hiddenColumns', N'kz', N'Жасырын бағандар көрсетілмеген: {count}'),
    (N'tablePreview.truncated', N'ru', N'Показаны только первые {shown} строк; ещё {more} не показаны.'),
    (N'tablePreview.truncated', N'kz', N'Тек алғашқы {shown} жол көрсетілген; тағы {more} жол көрсетілмеген.'),
    (N'tablePreview.ignoredRules', N'ru', N'Правила этой таблицы, которые не показаны (скрытый столбец, неизвестный оператор или неполное правило): {count}'),
    (N'tablePreview.ignoredRules', N'kz', N'Осы кестенің көрсетілмеген ережелері (жасырын баған, белгісіз оператор немесе толық емес ереже): {count}'),
    (N'tablePreview.monthsInColumns', N'ru', N'В документе эти столбцы повторяются для каждого месяца периода.'),
    (N'tablePreview.monthsInColumns', N'kz', N'Құжатта бұл бағандар кезеңнің әр айы үшін қайталанады.'),
    (N'tablePreview.monthsInRows', N'ru', N'В документе эти строки повторяются для каждого месяца периода.'),
    (N'tablePreview.monthsInRows', N'kz', N'Құжатта бұл жолдар кезеңнің әр айы үшін қайталанады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- CONSTRUCTOR:preview ── кінець секції ──

