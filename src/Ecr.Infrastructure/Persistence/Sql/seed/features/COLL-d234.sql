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

-- COLL:d234 ── Ширина колонки (D-234): відмова 422 і підписи форми колонки / скидання ширин у сітці ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.widthOutOfRange', N'en', N'Column width must be a whole number from 40 to 800 px, got "{widthPx}".', 1),
    (N'columns.width',                     N'en', N'Default width, px', 1),
    (N'columns.widthHint',                 N'en', N'From 40 to 800; empty means the default for the data type. A width a user sets in the grid overrides it.', 1),
    (N'columns.errWidth',                  N'en', N'Width must be a whole number from 40 to 800.', 1),
    (N'grid.columnWidths.reset',           N'en', N'Reset column widths', 1)
    -- ru/kz — окремою порцією `COLL:d234` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d234 ── кінець секції ──
