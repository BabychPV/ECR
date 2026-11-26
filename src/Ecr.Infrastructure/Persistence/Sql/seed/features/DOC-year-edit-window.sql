-- B14:year-edit-window ── ru/kz: b14 document-move (D-579, рішення людини 25.11) — «вікно редагування = рік проєкту + зміщення» шаблону; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'templates.yearEditWindow', N'ru', N'Редактирование годовых данных'),
    (N'templates.yearEditWindow', N'kz', N'Жылдық деректерді өңдеу'),
    (N'templates.yearEditWindowProjectYear', N'ru', N'Год проекта + льготный срок'),
    (N'templates.yearEditWindowProjectYear', N'kz', N'Жоба жылы + жеңілдік мерзімі'),
    (N'templates.yearEditWindowPeriodState', N'ru', N'По состоянию месяца'),
    (N'templates.yearEditWindowPeriodState', N'kz', N'Ай күйі бойынша'),
    (N'templates.yearEditWindowHint', N'ru', N'В месячном проекте годовые данные документа этого шаблона редактируются весь год проекта и льготный срок после него (до архивации), независимо от закрытых месяцев.'),
    (N'templates.yearEditWindowHint', N'kz', N'Айлық жобада осы үлгі құжатының жылдық деректері жобаның бүкіл жылы және одан кейінгі жеңілдік мерзімі бойы (мұрағаттауға дейін) жабық айларға қарамастан өңделеді.'),
    (N'templates.yearEditWindowOn', N'ru', N'Редактировать годовые данные до конца года проекта + льготного срока'),
    (N'templates.yearEditWindowOn', N'kz', N'Жылдық деректерді жоба жылы + жеңілдік мерзімі соңына дейін өңдеу'),
    (N'templates.yearEditWindowOff', N'ru', N'Редактировать годовые данные по состоянию месяца'),
    (N'templates.yearEditWindowOff', N'kz', N'Жылдық деректерді ай күйі бойынша өңдеу'),
    (N'templates.yearEditWindowSaved', N'ru', N'Правило редактирования годовых данных сохранено'),
    (N'templates.yearEditWindowSaved', N'kz', N'Жылдық деректерді өңдеу ережесі сақталды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- B14:year-edit-window ── кінець секції ──
-- B14:year-edit-window ── en; ru/kz — порцією B14:year-edit-window вище ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'templates.yearEditWindow', N'en', N'Year data editing', 1),
    (N'templates.yearEditWindowProjectYear', N'en', N'Project year + grace period', 1),
    (N'templates.yearEditWindowPeriodState', N'en', N'By month state', 1),
    (N'templates.yearEditWindowHint', N'en', N'In a monthly project, year data of this template''s document can be edited for the whole project year and the grace period after it (until archiving), regardless of closed months.', 1),
    (N'templates.yearEditWindowOn', N'en', N'Edit year data until the end of the project year + grace period', 1),
    (N'templates.yearEditWindowOff', N'en', N'Edit year data by month state', 1),
    (N'templates.yearEditWindowSaved', N'en', N'Year data editing rule saved', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- B14:year-edit-window ── кінець секції ──
