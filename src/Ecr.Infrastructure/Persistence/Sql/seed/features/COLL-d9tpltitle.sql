-- COLL:d9tpltitle ── ru/kz заголовка 409 на зайнятий код шаблону (D-9); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0409.templateCodeTaken.title', N'ru', N'Код шаблона уже используется'),
    (N'err.ECR-TMPL-0409.templateCodeTaken.title', N'kz', N'Үлгі коды бұрыннан қолданылады')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d9tpltitle ── кінець секції ──
-- COLL:d9tpltitle ── приймальна №8, D-9: власний заголовок 409 на зайнятий код шаблону; ru/kz — порцією COLL:d9tpltitle нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0409.templateCodeTaken.title', N'en', N'Template code is already in use', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d9tpltitle ── кінець секції ──
