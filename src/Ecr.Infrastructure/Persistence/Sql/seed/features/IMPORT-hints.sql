-- IMPORT:hints ── ru/kz підказок імпорту Excel (P3); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-CELL-4221.importCalculatedStale', N'ru', N'Система пересчитала эту ячейку после выгрузки книги, поэтому книга устарела. Выгрузите документ заново и повторите изменения в новой книге.'),
    (N'err.ECR-CELL-4221.importCalculatedStale', N'kz', N'Жүйе бұл ұяшықты кітап экспортталғаннан кейін қайта есептеді, сондықтан кітап ескірген. Құжатты қайта экспорттап, өзгерістерді жаңа кітапта қайталаңыз.'),
    (N'import.excelCell', N'ru', N'Ячейка в файле'),
    (N'import.excelCell', N'kz', N'Файлдағы ұяшық'),
    (N'import.recalculateTitle', N'ru', N'Результаты расчёта устарели'),
    (N'import.recalculateTitle', N'kz', N'Есептеу нәтижелері ескірген'),
    (N'import.recalculateHint', N'ru', N'Импорт изменил входные данные методики. Нажмите «{action}», чтобы обновить результаты расчёта.'),
    (N'import.recalculateHint', N'kz', N'Импорт әдістеменің кіріс деректерін өзгертті. Есептеу нәтижелерін жаңарту үшін «{action}» түймесін басыңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- IMPORT:hints ── кінець секції ──

