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
