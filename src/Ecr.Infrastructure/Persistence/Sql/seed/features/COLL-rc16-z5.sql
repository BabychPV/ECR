-- COLL:rc16-z5 ── ru/kz: помилка порожнього обов'язкового поля шапки; власна порція
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'document.header.requiredError', N'ru', N'Обязательное поле'),
    (N'document.header.requiredError', N'kz', N'Міндетті өріс')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:rc16-z5 ── кінець секції ──
