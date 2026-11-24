-- MS:only-editor-ui ── ru/kz: сторінка методології лише з версіями-скриптами (рішення людини 24.11 «має бути тільки наш новий редактор»): поточна версія-скрипт, «переноситься» для методології лише з формульними версіями; редактор скрипта: код на весь екран, одна проблема — текстом ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'methodologies.currentScript.title', N'ru', N'Текущая версия-скрипт'),
    (N'methodologies.currentScript.title', N'kz', N'Ағымдағы скрипт нұсқасы'),
    (N'methodologies.currentScript.hint', N'ru', N'Расчёты используют эту версию. Скрипт, его тесты и обязательные входные столбцы правятся в редакторе скрипта.'),
    (N'methodologies.currentScript.hint', N'kz', N'Есептеулер осы нұсқаны пайдаланады. Скрипт, оның тесттері және міндетті кіріс бағандары скрипт редакторында өзгертіледі.'),
    (N'methodologies.noVersions', N'ru', N'У этой методики нет версий'),
    (N'methodologies.noVersions', N'kz', N'Бұл әдістеменің нұсқалары жоқ'),
    (N'methodologies.noVersionsHint', N'ru', N'Расчёт выполняет именно версия. Чтобы начать, создайте черновик.'),
    (N'methodologies.noVersionsHint', N'kz', N'Есептеуді нақ нұсқа орындайды. Бастау үшін нобай құрыңыз.'),
    (N'methodologies.converting.title', N'ru', N'Методология переносится в редактор скрипта'),
    (N'methodologies.converting.title', N'kz', N'Әдістеме скрипт редакторына көшірілуде'),
    (N'methodologies.converting.hint', N'ru', N'Версии этой методологии ещё написаны для прежнего формульного движка и переносятся в скрипт. До окончания переноса расчёты идут как раньше, а редактировать методологию здесь нельзя.'),
    (N'methodologies.converting.hint', N'kz', N'Бұл әдістеменің нұсқалары әлі бұрынғы формула қозғалтқышына жазылған және скриптке көшірілуде. Көшіру аяқталғанша есептеулер бұрынғыдай жүреді, ал әдістемені мұнда өңдеуге болмайды.'),
    (N'methodologyScript.problemSingle', N'ru', N'Проблема в скрипте: {message}'),
    (N'methodologyScript.problemSingle', N'kz', N'Скриптегі мәселе: {message}'),
    (N'methodologyScript.fullScreen.label', N'ru', N'Код на весь экран'),
    (N'methodologyScript.fullScreen.label', N'kz', N'Код толық экранда'),
    (N'methodologyScript.fullScreen.enterHint', N'ru', N'Развернуть код на всё окно (Ctrl+Shift+F)'),
    (N'methodologyScript.fullScreen.enterHint', N'kz', N'Кодты бүкіл терезеге жаю (Ctrl+Shift+F)'),
    (N'methodologyScript.fullScreen.exitHint', N'ru', N'Выйти из полноэкранного режима (Esc или Ctrl+Shift+F)'),
    (N'methodologyScript.fullScreen.exitHint', N'kz', N'Толық экран режимінен шығу (Esc немесе Ctrl+Shift+F)')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- MS:only-editor-ui ── кінець секції ──

-- MS:only-editor-ui ── en: сторінка методології лише з версіями-скриптами; ru/kz — порцією MS:only-editor-ui вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'methodologies.currentScript.title', N'en', N'Current script version', 1),
    (N'methodologies.currentScript.hint', N'en', N'Calculations use this version. Edit the script, its tests and required input columns in the script editor.', 1),
    (N'methodologies.noVersions', N'en', N'This methodology has no versions', 1),
    (N'methodologies.noVersionsHint', N'en', N'A version is what actually calculates. Create a draft to start.', 1),
    (N'methodologies.converting.title', N'en', N'This methodology is being moved to the script editor', 1),
    (N'methodologies.converting.hint', N'en', N'Its versions are still written for the former formula engine and are being converted to a script. Until the conversion is done calculations run as before, and the methodology cannot be edited here.', 1),
    (N'methodologyScript.problemSingle', N'en', N'Problem in the script: {message}', 1),
    (N'methodologyScript.fullScreen.label', N'en', N'Code full screen', 1),
    (N'methodologyScript.fullScreen.enterHint', N'en', N'Expand the code to the whole window (Ctrl+Shift+F)', 1),
    (N'methodologyScript.fullScreen.exitHint', N'en', N'Exit full screen (Esc or Ctrl+Shift+F)', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- MS:only-editor-ui ── кінець секції ──
