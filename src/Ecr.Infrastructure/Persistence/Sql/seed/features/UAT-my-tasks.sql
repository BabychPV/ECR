-- UAT:my-tasks ── ru/kz: блоки «My tasks» згортаються, місяці одного аркуша — однією групою ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'myTasks.jobs.title', N'ru', N'Фоновые задачи'),
    (N'myTasks.approval.months.one', N'ru', N'{count} месяц'),
    (N'myTasks.approval.months.few', N'ru', N'{count} месяца'),
    (N'myTasks.approval.months.many', N'ru', N'{count} месяцев'),
    (N'myTasks.approval.months.other', N'ru', N'{count} месяца'),
    (N'myTasks.jobs.title', N'kz', N'Фондық тапсырмалар'),
    (N'myTasks.approval.months.one', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.few', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.many', N'kz', N'{count} ай'),
    (N'myTasks.approval.months.other', N'kz', N'{count} ай')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:my-tasks ── кінець секції ──
-- UAT:my-tasks ── блоки «My tasks» згортаються, місяці одного аркуша — однією групою (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'myTasks.jobs.title', N'en', N'Background tasks', 1),
    (N'myTasks.approval.months.one', N'en', N'{count} month', 1),
    (N'myTasks.approval.months.few', N'en', N'{count} months', 1),
    (N'myTasks.approval.months.many', N'en', N'{count} months', 1),
    (N'myTasks.approval.months.other', N'en', N'{count} months', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:my-tasks ── кінець секції ──
-- UAT:project-settings ── ru/kz: повні назви дій налаштувань проєкту з описами; пояснення автоматичного періоду ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'projectSettings.pinAction', N'ru', N'Закрепить текущий период…'),
    (N'projectSettings.pinActionDesc', N'ru', N'Сделать выбранный месяц текущим по умолчанию для всех в проекте, с причиной. Месяцы не открывает и не закрывает.'),
    (N'projectSettings.cloneAction', N'ru', N'Клонировать проект…'),
    (N'projectSettings.cloneActionDesc', N'ru', N'Создать новый проект с теми же шаблонами, справочниками и настройками. Данные не копируются.'),
    (N'projectSettings.archiveAction', N'ru', N'Архивировать проект…'),
    (N'projectSettings.archiveActionDesc', N'ru', N'Отметить проект завершённым. Можно, только когда все периоды закрыты; отменить нельзя.'),
    (N'projectSettings.routeAction', N'ru', N'Изменить маршрут согласования…'),
    (N'projectSettings.routeActionDesc', N'ru', N'Выбрать, какие роли и в каком порядке утверждают поданные листы.'),
    (N'projectSettings.policiesAction', N'ru', N'Управлять политиками периодов…'),
    (N'projectSettings.policiesActionDesc', N'ru', N'Задать, когда месяцы открываются, переходят в льготный срок и закрываются (смещения в днях); политики общие для проектов.'),
    (N'projectSettings.autoIs', N'ru', N'Автоматический режим берёт самый ранний открытый месяц: {period}.'),
    (N'projectSettings.autoIsEarlier', N'ru', N'Автоматический режим берёт самый ранний открытый месяц, а не текущий по дате: {period}. Сегодня {today}, но {period} ещё открыт, поэтому он первый. Закройте более ранние месяцы на странице Periods, чтобы перейти дальше.'),
    (N'projectSettings.autoIsLater', N'ru', N'Автоматический режим берёт самый ранний открытый месяц: {period}. Месяц по сегодняшней дате, {today}, в календаре проекта не открыт.'),
    (N'projectSettings.autoNone', N'ru', N'Автоматический режим: сейчас нет открытого месяца, поэтому текущего периода нет.'),
    (N'projectSettings.pinAction', N'kz', N'Ағымдағы кезеңді бекіту…'),
    (N'projectSettings.pinActionDesc', N'kz', N'Таңдалған айды жобадағы барлығы үшін әдепкі ағымдағы ай ету, себебімен. Айларды ашпайды да, жаппайды да.'),
    (N'projectSettings.cloneAction', N'kz', N'Жобаны көшіру…'),
    (N'projectSettings.cloneActionDesc', N'kz', N'Сол үлгілер, анықтамалықтар және баптаулармен жаңа жоба құру. Деректер көшірілмейді.'),
    (N'projectSettings.archiveAction', N'kz', N'Жобаны мұрағаттау…'),
    (N'projectSettings.archiveActionDesc', N'kz', N'Жобаны аяқталған деп белгілеу. Барлық кезеңдер жабылғанда ғана мүмкін; қайтару мүмкін емес.'),
    (N'projectSettings.routeAction', N'kz', N'Келісу маршрутын өзгерту…'),
    (N'projectSettings.routeActionDesc', N'kz', N'Берілген парақтарды қай рөлдер және қандай ретпен бекітетінін таңдау.'),
    (N'projectSettings.policiesAction', N'kz', N'Кезең саясаттарын басқару…'),
    (N'projectSettings.policiesActionDesc', N'kz', N'Айлардың қашан ашылатынын, жеңілдік мерзіміне өтетінін және жабылатынын белгілеу (күндермен ығысу); саясаттар жобаларға ортақ.'),
    (N'projectSettings.autoIs', N'kz', N'Автоматты режим ең ерте ашық айды алады: {period}.'),
    (N'projectSettings.autoIsEarlier', N'kz', N'Автоматты режим бүгінгі күн бойынша айды емес, ең ерте ашық айды алады: {period}. Бүгін {today}, бірақ {period} әлі ашық, сондықтан ол бірінші. Әрі қарай өту үшін ертерек айларды Periods бетінде жабыңыз.'),
    (N'projectSettings.autoIsLater', N'kz', N'Автоматты режим ең ерте ашық айды алады: {period}. Бүгінгі күн бойынша ай, {today}, жоба күнтізбесінде ашық емес.'),
    (N'projectSettings.autoNone', N'kz', N'Автоматты режим: қазір ашық ай жоқ, сондықтан ағымдағы кезең жоқ.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- UAT:project-settings ── кінець секції ──
-- UAT:project-settings ── повні назви дій налаштувань проєкту з описами; пояснення автоматичного періоду (en) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'projectSettings.pinAction', N'en', N'Pin current period…', 1),
    (N'projectSettings.pinActionDesc', N'en', N'Make a chosen month the default current month for everyone in this project, with a reason. It does not open or close months.', 1),
    (N'projectSettings.cloneAction', N'en', N'Clone project…', 1),
    (N'projectSettings.cloneActionDesc', N'en', N'Create a new project with the same templates, reference data and settings. Data is not copied.', 1),
    (N'projectSettings.archiveAction', N'en', N'Archive project…', 1),
    (N'projectSettings.archiveActionDesc', N'en', N'Mark the project as finished. Allowed only when every period is closed; cannot be undone.', 1),
    (N'projectSettings.routeAction', N'en', N'Edit approval route…', 1),
    (N'projectSettings.routeActionDesc', N'en', N'Choose which roles approve submitted sheets, and in what order.', 1),
    (N'projectSettings.policiesAction', N'en', N'Manage period policies…', 1),
    (N'projectSettings.policiesActionDesc', N'en', N'Set when months open, move to grace and close (offsets in days); policies are shared by projects.', 1),
    (N'projectSettings.autoIs', N'en', N'Automatic mode picks the earliest open month: {period}.', 1),
    (N'projectSettings.autoIsEarlier', N'en', N'Automatic mode picks the earliest open month, not today''s month: {period}. Today is {today}, but {period} is still open, so it comes first. Close earlier months on the Periods page to move on.', 1),
    (N'projectSettings.autoIsLater', N'en', N'Automatic mode picks the earliest open month: {period}. Today''s month, {today}, is not open in this project''s calendar.', 1),
    (N'projectSettings.autoNone', N'en', N'Automatic mode: no month is open now, so there is no current period.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- UAT:project-settings ── кінець секції ──
