-- COLL:a212 ── ru/kz A2-12: поля шапки документа в експорті; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'export.header.sheet', N'ru', N'Шапка документа'),
    (N'export.header.field', N'ru', N'Поле'),
    (N'export.header.code', N'ru', N'Код'),
    (N'export.header.value', N'ru', N'Значение'),
    (N'export.header.sheet', N'kz', N'Құжаттың тақырып бөлігі'),
    (N'export.header.field', N'kz', N'Өріс'),
    (N'export.header.code', N'kz', N'Код'),
    (N'export.header.value', N'kz', N'Мәні')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a212 ── кінець секції ──
-- COLL:a212 ── A2-12: поля шапки документа в експорті (аркуш «Document header» у xlsx, ✎ D-477 — було «Header», оновлення наявних баз — XLS-header-sheet.sql); ru/kz — порцією COLL:a212 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'export.header.sheet', N'en', N'Document header', 1),
    (N'export.header.field', N'en', N'Field', 1),
    (N'export.header.code', N'en', N'Code', 1),
    (N'export.header.value', N'en', N'Value', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:a212 ── кінець секції ──
