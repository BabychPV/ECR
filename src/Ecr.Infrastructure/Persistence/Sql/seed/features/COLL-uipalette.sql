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
