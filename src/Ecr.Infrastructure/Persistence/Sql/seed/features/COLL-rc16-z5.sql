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
-- COLL:rc16-z5 ── помилка порожнього обов'язкового поля шапки; ru/kz — порцією COLL:rc16-z5 нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'document.header.requiredError', N'en', N'This field is required', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:rc16-z5 ── кінець секції ──
