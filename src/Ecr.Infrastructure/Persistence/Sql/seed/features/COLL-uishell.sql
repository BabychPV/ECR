-- COLL:uishell ── ru/kz: UI-11/UI-12: групи бічного меню й пояснення екранів під заголовком (макет docs/design/hybrid); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'nav.group.work', N'ru', N'Работа'),
    (N'nav.group.work', N'kz', N'Жұмыс'),
    (N'nav.group.configure', N'ru', N'Настройка'),
    (N'nav.group.configure', N'kz', N'Баптау'),
    (N'nav.group.access', N'ru', N'Доступ'),
    (N'nav.group.access', N'kz', N'Қолжетімділік'),
    (N'nav.group.operate', N'ru', N'Эксплуатация'),
    (N'nav.group.operate', N'kz', N'Пайдалану'),
    (N'nav.documents.description', N'ru', N'Отчёты ваших проектов за выбранный год. Откройте документ, чтобы работать с ним.'),
    (N'nav.documents.description', N'kz', N'Таңдалған жылдағы жобаларыңыздың есептері. Онымен жұмыс істеу үшін құжатты ашыңыз.'),
    (N'nav.myGroups.description', N'ru', N'Откуда берётся ваш доступ: группы каталога дают роли, роли дают права и проекты. Здесь ничего нельзя изменить; это делает администратор.'),
    (N'nav.myGroups.description', N'kz', N'Қолжетімділігіңіз қайдан келеді: каталог топтары рөлдер береді, рөлдер құқықтар мен жобалар береді. Мұнда ештеңені өзгертуге болмайды; оны әкімші өзгертеді.'),
    (N'nav.templates.description', N'ru', N'Шаблон — это структура отчёта: листы, таблицы, колонки, формулы и правила. Документы создаются из опубликованной версии и остаются на ней.'),
    (N'nav.templates.description', N'kz', N'Үлгі — есептің құрылымы: парақтар, кестелер, бағандар, формулалар мен ережелер. Құжаттар жарияланған нұсқадан жасалады және сол нұсқада қалады.'),
    (N'nav.registries.description', N'ru', N'Справочники, из которых ячейки, формулы и сопоставления берут значения: источники, вещества, топливо, лимиты.'),
    (N'nav.registries.description', N'kz', N'Ұяшықтар, формулалар мен сәйкестендірулер мән алатын анықтамалықтар: көздер, заттар, отын, лимиттер.'),
    (N'nav.methodologies.description', N'ru', N'Как рассчитываются выбросы: формулы, константы и правила, связывающие их со строками таблиц. Документы считаются по опубликованной версии.'),
    (N'nav.methodologies.description', N'kz', N'Шығарындылар қалай есептеледі: формулалар, тұрақтылар және оларды кесте жолдарымен байланыстыратын ережелер. Құжаттар жарияланған нұсқа бойынша есептеледі.'),
    (N'nav.units.description', N'ru', N'Единицы измерения, сгруппированные по величинам. Каждая единица переводится в базовую единицу своей величины, поэтому значения из источников и ячеек можно безопасно сравнивать.'),
    (N'nav.units.description', N'kz', N'Шамалар бойынша топталған өлшем бірліктері. Әр бірлік өз шамасының негізгі бірлігіне ауыстырылады, сондықтан көздер мен ұяшықтардағы мәндерді қауіпсіз салыстыруға болады.'),
    (N'nav.sources.description', N'ru', N'Откуда берутся собранные значения: PI System и базы данных SQL. Каждый источник работает по своему расписанию.'),
    (N'nav.sources.description', N'kz', N'Жиналған мәндер қайдан келеді: PI System және SQL дерекқорлары. Әр көз өз кестесі бойынша жұмыс істейді.'),
    (N'nav.mapping.description', N'ru', N'Какой атрибут источника заполняет какую ячейку, на реальных значениях: неверная единица или строка видна до того, как попадёт в отчёт.'),
    (N'nav.mapping.description', N'kz', N'Көздің қай атрибуты қай ұяшықты толтырады, нақты мәндерде: қате бірлік немесе жол есепке түспей тұрып көрінеді.'),
    (N'nav.periods.description', N'ru', N'Период — один отчётный месяц проекта. Пока он открыт, данные можно вводить; после срока он закрывается, а позже архивируется.'),
    (N'nav.periods.description', N'kz', N'Кезең — жобаның бір есепті айы. Ол ашық тұрғанда деректерді енгізуге болады; мерзімнен кейін ол жабылады, кейінірек мұрағатталады.'),
    (N'nav.jobs.description', N'ru', N'Экспорт, импорт, пересчёты и ночные проверки выполняются в фоне. У задачи с ошибкой видно, почему она не выполнилась.'),
    (N'nav.jobs.description', N'kz', N'Экспорт, импорт, қайта есептеулер және түнгі тексерулер фонда орындалады. Қатемен аяқталған тапсырманың неге орындалмағаны көрінеді.'),
    (N'nav.snapshots.description', N'ru', N'Снимок — точная копия отчёта в момент его создания. Он не меняется, даже если документы потом редактируют.'),
    (N'nav.snapshots.description', N'kz', N'Сурет — есептің жасалған сәттегі дәл көшірмесі. Құжаттар кейін өзгертілсе де, ол өзгермейді.'),
    (N'nav.audit.description', N'ru', N'Каждое изменение каждого числа: кто, когда, что было и что стало. Здесь ничего нельзя изменить или удалить.'),
    (N'nav.audit.description', N'kz', N'Әр санның әр өзгерісі: кім, қашан, бұрын не еді және не болды. Мұнда ештеңені өзгертуге немесе жоюға болмайды.'),
    (N'nav.consistency.description', N'ru', N'Каждую ночь ECR сверяет числа, которые должны совпадать между листами, документами и справочниками. Каждое расхождение показано здесь.'),
    (N'nav.consistency.description', N'kz', N'ECR әр түнде парақтар, құжаттар мен анықтамалықтар арасында сәйкес келуі тиіс сандарды салыстырады. Әр сәйкессіздік осында көрсетіледі.'),
    (N'nav.uiStrings.description', N'ru', N'Все подписи, сообщения и тексты кнопок ECR. Источник — английский; если перевода нет, показывается английский текст.'),
    (N'nav.uiStrings.description', N'kz', N'ECR-дің барлық белгілері, хабарлары мен түйме мәтіндері. Бастапқы тіл — ағылшын; аудармасы жоқ болса, ағылшынша мәтін көрсетіледі.'),
    (N'nav.health.description', N'ru', N'Работает ли ECR прямо сейчас? Предупреждения появляются, только когда нужно что-то сделать.'),
    (N'nav.health.description', N'kz', N'ECR дәл қазір жұмыс істеп тұр ма? Ескертулер бір нәрсе істеу керек болғанда ғана шығады.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uishell ── кінець секції ──
-- COLL:uishell ── UI-11/UI-12: групи бічного меню й пояснення екранів під заголовком (макет docs/design/hybrid); ru/kz — порцією COLL:uishell у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'nav.group.work', N'en', N'Work', 1),
    (N'nav.group.configure', N'en', N'Configure', 1),
    (N'nav.group.access', N'en', N'Access', 1),
    (N'nav.group.operate', N'en', N'Operate', 1),
    (N'nav.documents.description', N'en', N'Reports of your projects for the selected year. Open a document to work in it.', 1),
    (N'nav.myGroups.description', N'en', N'Where your access comes from: directory groups give you roles, roles give you permissions and projects. Nothing here can be edited; an administrator changes it for you.', 1),
    (N'nav.templates.description', N'en', N'A template is the structure of a report: sheets, tables, columns, formulas and rules. Documents are created from a published version and stay on it.', 1),
    (N'nav.registries.description', N'en', N'Reference lists that cells, formulas and mappings pick values from: sources, substances, fuels, limits.', 1),
    (N'nav.methodologies.description', N'en', N'How emissions are calculated: formulas, constants and the rules that bind them to table rows. A published version is what documents calculate with.', 1),
    (N'nav.units.description', N'en', N'Units of measure grouped by dimension. Each unit converts to the base unit of its dimension, so values from sources and cells can be compared safely.', 1),
    (N'nav.sources.description', N'en', N'Where collected values come from: the PI System and SQL databases. Each source runs on its own schedule.', 1),
    (N'nav.mapping.description', N'en', N'Which source attribute fills which cell, shown on real values, so a wrong unit or a wrong row is visible before it reaches a report.', 1),
    (N'nav.periods.description', N'en', N'A period is one reporting month of a project. While it is open, data can be entered; after the deadline it closes, and later it is archived.', 1),
    (N'nav.jobs.description', N'en', N'Exports, imports, recalculations and nightly checks run in the background. A failed job shows why it failed.', 1),
    (N'nav.snapshots.description', N'en', N'A snapshot is the exact copy of a report at the moment it was made. It never changes, even if the documents are edited later.', 1),
    (N'nav.audit.description', N'en', N'Every change of every number: who, when, what it was and what it became. Nothing here can be edited or deleted.', 1),
    (N'nav.consistency.description', N'en', N'Every night ECR compares numbers that must agree with each other across sheets, documents and registries. Each finding is listed here.', 1),
    (N'nav.uiStrings.description', N'en', N'Every label, message and button text of ECR. English is the source; missing translations fall back to English.', 1),
    (N'nav.health.description', N'en', N'Is ECR working right now? Warnings appear only when there is something to do.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:uishell ── кінець секції ──
-- COLL:uishell ── змінені тексти наявних ключів (Pass2 п. 7, людський прохід 07.11): перелік документів — перегляд РОКУ
-- (Period clarity), «за один період» під «One document per object; all months are inside it» суперечило екрану.
-- Епілог оновлює, лише поки в базі стоїть САМЕ старе значення.
INSERT INTO #UiChanged ([Key], Lang, OldVal, NewVal)
SELECT v.[Key], v.Lang, v.OldVal, v.NewVal
  FROM (VALUES
    (N'nav.documents.description', N'en', N'Reports of your projects for one period. Open a document to work in it.', N'Reports of your projects for the selected year. Open a document to work in it.'),
    (N'nav.documents.description', N'ru', N'Отчёты ваших проектов за один период. Откройте документ, чтобы работать с ним.', N'Отчёты ваших проектов за выбранный год. Откройте документ, чтобы работать с ним.'),
    (N'nav.documents.description', N'kz', N'Бір кезеңдегі жобаларыңыздың есептері. Онымен жұмыс істеу үшін құжатты ашыңыз.', N'Таңдалған жылдағы жобаларыңыздың есептері. Онымен жұмыс істеу үшін құжатты ашыңыз.')
       ) AS v ([Key], Lang, OldVal, NewVal)
OPTION (RECOMPILE);
GO
-- COLL:uishell ── кінець секції ──
