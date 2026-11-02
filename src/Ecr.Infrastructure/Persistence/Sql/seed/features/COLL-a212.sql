-- COLL:a212 ── ru/kz A2-12: поля шапки документа в експорті; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'export.header.sheet', N'ru', N'Шапка'),
    (N'export.header.field', N'ru', N'Поле'),
    (N'export.header.code', N'ru', N'Код'),
    (N'export.header.value', N'ru', N'Значение'),
    (N'export.header.sheet', N'kz', N'Тақырып'),
    (N'export.header.field', N'kz', N'Өріс'),
    (N'export.header.code', N'kz', N'Код'),
    (N'export.header.value', N'kz', N'Мәні')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:a212 ── кінець секції ──
