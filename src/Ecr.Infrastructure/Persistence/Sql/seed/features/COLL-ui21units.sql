-- COLL:ui21units ── ru/kz UI-21: довідник одиниць на шаблоні переліку; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'units.description', N'ru', N'Единицы измерения, сгруппированные по размерности. Каждая единица пересчитывается в базовую единицу своей размерности, поэтому значения из источников и ячеек можно безопасно сравнивать.'),
    (N'units.description', N'kz', N'Өлшем бойынша топтастырылған өлшем бірліктері. Әр бірлік өз өлшемінің базалық бірлігіне қайта есептеледі, сондықтан көздер мен ұяшықтардағы мәндерді қауіпсіз салыстыруға болады.'),
    (N'units.statsLabel', N'ru', N'Единицы — сводка'),
    (N'units.statsLabel', N'kz', N'Бірліктер — қысқаша'),
    (N'units.statUnits', N'ru', N'единиц'),
    (N'units.statUnits', N'kz', N'бірлік'),
    (N'units.statDimensions', N'ru', N'размерностей'),
    (N'units.statDimensions', N'kz', N'өлшем'),
    (N'units.statOffset', N'ru', N'со смещением (температура)'),
    (N'units.statOffset', N'kz', N'ығысуы бар (температура)'),
    (N'units.search', N'ru', N'Поиск'),
    (N'units.search', N'kz', N'Іздеу'),
    (N'units.searchPlaceholder', N'ru', N'Код или размерность'),
    (N'units.searchPlaceholder', N'kz', N'Код немесе өлшем'),
    (N'units.baseUnit', N'ru', N'Базовая единица'),
    (N'units.baseUnit', N'kz', N'Базалық бірлік'),
    (N'units.checkConversion', N'ru', N'Проверить пересчёт'),
    (N'units.checkConversion', N'kz', N'Қайта есептеуді тексеру'),
    (N'units.swap', N'ru', N'Поменять местами'),
    (N'units.swap', N'kz', N'Орындарын ауыстыру'),
    (N'units.conversion', N'ru', N'Пересчёт'),
    (N'units.conversion', N'kz', N'Қайта есептеу'),
    (N'units.whereUsed', N'ru', N'Где используется'),
    (N'units.whereUsed', N'kz', N'Қай жерде қолданылады'),
    (N'units.noMatch', N'ru', N'Нет единиц, подходящих под фильтры.'),
    (N'units.noMatch', N'kz', N'Сүзгілерге сәйкес бірліктер жоқ.'),
    (N'units.usedIn', N'ru', N'Где используется'),
    (N'units.usedIn', N'kz', N'Қолданылатын жері'),
    (N'units.statUnused', N'ru', N'нигде не используются'),
    (N'units.statUnused', N'kz', N'еш жерде қолданылмайды')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:ui21units ── кінець секції ──
