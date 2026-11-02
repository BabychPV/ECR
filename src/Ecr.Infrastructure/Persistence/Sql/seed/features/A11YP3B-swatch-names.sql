-- A11Y-P3B:swatch-names ── ru/kz: CL-23 (B-9), назви кольорів палітри; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'grid.cellColor.name.white', N'ru', N'Белый'),
    (N'grid.cellColor.name.white', N'kz', N'Ақ'),
    (N'grid.cellColor.name.black', N'ru', N'Чёрный'),
    (N'grid.cellColor.name.black', N'kz', N'Қара'),
    (N'grid.cellColor.name.lightGray', N'ru', N'Светло-серый'),
    (N'grid.cellColor.name.lightGray', N'kz', N'Ашық сұр'),
    (N'grid.cellColor.name.blueGray', N'ru', N'Серо-синий'),
    (N'grid.cellColor.name.blueGray', N'kz', N'Сұр-көк'),
    (N'grid.cellColor.name.blue', N'ru', N'Синий'),
    (N'grid.cellColor.name.blue', N'kz', N'Көк'),
    (N'grid.cellColor.name.orangeHue', N'ru', N'Оранжевый'),
    (N'grid.cellColor.name.orangeHue', N'kz', N'Қызғылт сары'),
    (N'grid.cellColor.name.gray', N'ru', N'Серый'),
    (N'grid.cellColor.name.gray', N'kz', N'Сұр'),
    (N'grid.cellColor.name.amber', N'ru', N'Янтарный'),
    (N'grid.cellColor.name.amber', N'kz', N'Сарғыш'),
    (N'grid.cellColor.name.skyBlue', N'ru', N'Небесно-голубой'),
    (N'grid.cellColor.name.skyBlue', N'kz', N'Аспан көк'),
    (N'grid.cellColor.name.mediumGreen', N'ru', N'Средне-зелёный'),
    (N'grid.cellColor.name.mediumGreen', N'kz', N'Орташа жасыл'),
    (N'grid.cellColor.name.darkRed', N'ru', N'Тёмно-красный'),
    (N'grid.cellColor.name.darkRed', N'kz', N'Қою қызыл'),
    (N'grid.cellColor.name.redHue', N'ru', N'Красный'),
    (N'grid.cellColor.name.redHue', N'kz', N'Қызыл'),
    (N'grid.cellColor.name.yellow', N'ru', N'Жёлтый'),
    (N'grid.cellColor.name.yellow', N'kz', N'Сары'),
    (N'grid.cellColor.name.lightGreen', N'ru', N'Светло-зелёный'),
    (N'grid.cellColor.name.lightGreen', N'kz', N'Ашық жасыл'),
    (N'grid.cellColor.name.green', N'ru', N'Зелёный'),
    (N'grid.cellColor.name.green', N'kz', N'Жасыл'),
    (N'grid.cellColor.name.cyan', N'ru', N'Голубой'),
    (N'grid.cellColor.name.cyan', N'kz', N'Көгілдір'),
    (N'grid.cellColor.name.strongBlue', N'ru', N'Насыщенно-синий'),
    (N'grid.cellColor.name.strongBlue', N'kz', N'Қанық көк'),
    (N'grid.cellColor.name.darkBlue', N'ru', N'Тёмно-синий'),
    (N'grid.cellColor.name.darkBlue', N'kz', N'Қою көк'),
    (N'grid.cellColor.name.purple', N'ru', N'Фиолетовый'),
    (N'grid.cellColor.name.purple', N'kz', N'Күлгін'),
    (N'grid.cellColor.shade.lighter80', N'ru', N'светлее на 80 %'),
    (N'grid.cellColor.shade.lighter80', N'kz', N'80 % ашығырақ'),
    (N'grid.cellColor.shade.lighter60', N'ru', N'светлее на 60 %'),
    (N'grid.cellColor.shade.lighter60', N'kz', N'60 % ашығырақ'),
    (N'grid.cellColor.shade.lighter40', N'ru', N'светлее на 40 %'),
    (N'grid.cellColor.shade.lighter40', N'kz', N'40 % ашығырақ'),
    (N'grid.cellColor.shade.darker25', N'ru', N'темнее на 25 %'),
    (N'grid.cellColor.shade.darker25', N'kz', N'25 % қарайғырақ'),
    (N'grid.cellColor.shade.darker50', N'ru', N'темнее на 50 %'),
    (N'grid.cellColor.shade.darker50', N'kz', N'50 % қарайғырақ'),
    (N'grid.cellColor.shadeName', N'ru', N'{name}, {shade}'),
    (N'grid.cellColor.shadeName', N'kz', N'{name}, {shade}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- A11Y-P3B:swatch-names ── кінець секції ──

-- A11Y-P3B:swatch-names ── CL-23 (B-9): назви кольорів палітри для читалки; ru/kz - порцією A11Y-P3B:swatch-names нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'grid.cellColor.name.white', N'en', N'White', 1),
    (N'grid.cellColor.name.black', N'en', N'Black', 1),
    (N'grid.cellColor.name.lightGray', N'en', N'Light gray', 1),
    (N'grid.cellColor.name.blueGray', N'en', N'Blue-gray', 1),
    (N'grid.cellColor.name.blue', N'en', N'Blue', 1),
    (N'grid.cellColor.name.orangeHue', N'en', N'Orange', 1),
    (N'grid.cellColor.name.gray', N'en', N'Gray', 1),
    (N'grid.cellColor.name.amber', N'en', N'Amber', 1),
    (N'grid.cellColor.name.skyBlue', N'en', N'Sky blue', 1),
    (N'grid.cellColor.name.mediumGreen', N'en', N'Medium green', 1),
    (N'grid.cellColor.name.darkRed', N'en', N'Dark red', 1),
    (N'grid.cellColor.name.redHue', N'en', N'Red', 1),
    (N'grid.cellColor.name.yellow', N'en', N'Yellow', 1),
    (N'grid.cellColor.name.lightGreen', N'en', N'Light green', 1),
    (N'grid.cellColor.name.green', N'en', N'Green', 1),
    (N'grid.cellColor.name.cyan', N'en', N'Cyan', 1),
    (N'grid.cellColor.name.strongBlue', N'en', N'Strong blue', 1),
    (N'grid.cellColor.name.darkBlue', N'en', N'Dark blue', 1),
    (N'grid.cellColor.name.purple', N'en', N'Purple', 1),
    (N'grid.cellColor.shade.lighter80', N'en', N'lighter 80%', 1),
    (N'grid.cellColor.shade.lighter60', N'en', N'lighter 60%', 1),
    (N'grid.cellColor.shade.lighter40', N'en', N'lighter 40%', 1),
    (N'grid.cellColor.shade.darker25', N'en', N'darker 25%', 1),
    (N'grid.cellColor.shade.darker50', N'en', N'darker 50%', 1),
    (N'grid.cellColor.shadeName', N'en', N'{name}, {shade}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- A11Y-P3B:swatch-names ── кінець секції ──
