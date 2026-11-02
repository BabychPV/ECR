-- PERMITS: groups-expand ── ru/kz: імпорт Excel - неоднозначний номер дозволу в групі (PRMG); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-0422.importAmbiguousPermitNumber', N'ru', N'Такой номер разрешения есть в нескольких типах разрешений группы столбца. Укажите его как «номер — тип» (кандидаты перечислены в сообщении).'),
    (N'err.ECR-CELL-0422.importAmbiguousPermitNumber', N'kz', N'Бұл рұқсат нөмірі баған тобының бірнеше рұқсат түрінде бар. Оны «нөмір — түр» түрінде жазыңыз (үміткерлер хабарламада көрсетілген).')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- PERMITS: groups-expand ── кінець секції ──
-- PERMITS: groups-expand ── en: імпорт Excel - номер дозволу збігається в кількох типах групи (PRMG); ru/kz — порцією PERMITS: groups-expand нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CELL-0422.importAmbiguousPermitNumber', N'en', N'This permit number exists in several permit types of the column''s group. Write it as "number — type" (the candidates are listed in the message).', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- PERMITS: groups-expand ── кінець секції ──
