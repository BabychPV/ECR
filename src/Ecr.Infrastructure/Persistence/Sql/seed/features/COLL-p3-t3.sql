-- COLL:p3-t3 ── ru/kz пакета P3 тестувального проходу №3; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0409.relationCodeTaken.title', N'ru', N'Код связи уже используется'),
    (N'err.ECR-TMPL-0409.relationCodeTaken.title', N'kz', N'Байланыс коды бұрыннан қолданылады'),
    (N'grid.tableLoadNow', N'ru', N'Загрузить сейчас'),
    (N'grid.tableLoadNow', N'kz', N'Қазір жүктеу')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p3-t3 ── кінець секції ──
-- COLL:p3-t3 ── тестувальний прохід №3, пакет P3 (T3-07…); ru/kz — порцією COLL:p3-t3 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0409.relationCodeTaken.title', N'en', N'Relation code is already in use', 1),
    (N'grid.tableLoadNow', N'en', N'Load now', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p3-t3 ── кінець секції ──
