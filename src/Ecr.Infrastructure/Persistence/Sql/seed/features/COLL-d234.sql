-- COLL:d234 ── ru/kz ширини колонки (D-234); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.widthOutOfRange', N'ru', N'Ширина столбца должна быть целым числом от 40 до 800 px, получено "{widthPx}".'),
    (N'err.ECR-TMPL-0422.widthOutOfRange', N'kz', N'Баған ені 40-тан 800 px-ке дейінгі бүтін сан болуы керек, алынған мән "{widthPx}".'),
    (N'columns.width', N'ru', N'Ширина по умолчанию, px'),
    (N'columns.width', N'kz', N'Әдепкі ені, px'),
    (N'columns.widthHint', N'ru', N'От 40 до 800; пусто — типовая ширина для типа данных. Ширина, заданная пользователем в таблице, перекрывает эту.'),
    (N'columns.widthHint', N'kz', N'40-тан 800-ге дейін; бос — деректер түрінің әдепкі ені. Пайдаланушы кестеде орнатқан ені мұны басады.'),
    (N'columns.errWidth', N'ru', N'Ширина должна быть целым числом от 40 до 800.'),
    (N'columns.errWidth', N'kz', N'Ен 40-тан 800-ге дейінгі бүтін сан болуы керек.'),
    (N'grid.columnWidths.reset', N'ru', N'Сбросить ширину столбцов'),
    (N'grid.columnWidths.reset', N'kz', N'Баған енін қалпына келтіру')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d234 ── кінець секції ──

