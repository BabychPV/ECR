-- COLL:uipalette ── ru/kz: UI-30 командна палітра: екрани й дії; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'palette.title', N'ru', N'Командная палитра'),
    (N'palette.title', N'kz', N'Пәрмендер палитрасы'),
    (N'palette.screens', N'ru', N'Экраны'),
    (N'palette.screens', N'kz', N'Экрандар'),
    (N'palette.actions', N'ru', N'Действия'),
    (N'palette.actions', N'kz', N'Әрекеттер'),
    (N'palette.hintMove', N'ru', N'перейти'),
    (N'palette.hintMove', N'kz', N'жылжу'),
    (N'palette.hintOpen', N'ru', N'открыть'),
    (N'palette.hintOpen', N'kz', N'ашу'),
    (N'palette.action.themeDark', N'ru', N'Переключить на тёмную тему'),
    (N'palette.action.themeDark', N'kz', N'Қараңғы тақырыпқа ауысу'),
    (N'palette.action.themeLight', N'ru', N'Переключить на светлую тему'),
    (N'palette.action.themeLight', N'kz', N'Ашық тақырыпқа ауысу'),
    (N'palette.action.densityCompact', N'ru', N'Компактные строки'),
    (N'palette.action.densityCompact', N'kz', N'Ықшам жолдар'),
    (N'palette.action.densityComfortable', N'ru', N'Просторные строки'),
    (N'palette.action.densityComfortable', N'kz', N'Кең жолдар'),
    (N'palette.action.collapseMenu', N'ru', N'Свернуть меню до значков'),
    (N'palette.action.collapseMenu', N'kz', N'Мәзірді белгішелерге жию'),
    (N'palette.action.expandMenu', N'ru', N'Развернуть меню'),
    (N'palette.action.expandMenu', N'kz', N'Мәзірді жаю')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:uipalette ── кінець секції ──
-- COLL:uipalette ── UI-30 командна палітра: екрани й дії; ru/kz — порцією COLL:uipalette у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'palette.title', N'en', N'Command palette', 1),
    (N'palette.screens', N'en', N'Screens', 1),
    (N'palette.actions', N'en', N'Actions', 1),
    (N'palette.hintMove', N'en', N'move', 1),
    (N'palette.hintOpen', N'en', N'open', 1),
    (N'palette.action.themeDark', N'en', N'Switch to dark theme', 1),
    (N'palette.action.themeLight', N'en', N'Switch to light theme', 1),
    (N'palette.action.densityCompact', N'en', N'Use compact rows', 1),
    (N'palette.action.densityComfortable', N'en', N'Use comfortable rows', 1),
    (N'palette.action.collapseMenu', N'en', N'Collapse menu to icons', 1),
    (N'palette.action.expandMenu', N'en', N'Expand menu', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:uipalette ── кінець секції ──
