-- COLL:p2-t2 ── ru/kz тестувального проходу №2 (T2-03); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0409.relationCodeTaken', N'ru', N'Связь таблиц с кодом «{relationCode}» уже существует. Коды связей уникальны во всех шаблонах и версиях: выберите другой код.'),
    (N'err.ECR-TMPL-0409.relationCodeTaken', N'kz', N'«{relationCode}» кодты кесте байланысы бұрыннан бар. Байланыс кодтары барлық үлгілер мен нұсқалар бойынша бірегей: басқа код таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:p2-t2 ── кінець секції ──
-- COLL:p2-t2 ── тестувальний прохід №2, T2-03: код зв'язку таблиць унікальний на всю систему; ru/kz — порцією COLL:p2-t2 у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0409.relationCodeTaken', N'en', N'A table relation with code "{relationCode}" already exists. Relation codes are unique across all templates and versions: choose a different code.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:p2-t2 ── кінець секції ──
