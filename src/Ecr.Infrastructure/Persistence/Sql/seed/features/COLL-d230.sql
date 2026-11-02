-- COLL:d230 ── ru/kz відмови схеми зв'язку Rollup/Check (D-230); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-TMPL-0422.relationSpecInvalid', N'ru', N'Связь «{relationCode}»: {detail}'),
    (N'err.ECR-TMPL-0422.relationSpecInvalid', N'kz', N'«{relationCode}» байланысы: {detail}')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d230 ── кінець секції ──

-- COLL:d230 ── Види зв'язків таблиць Rollup/Check (D-230, ФВ-2.12): відмова PUT …/relations/{code} за схемою MatchJson/MapJson (ПРИПУЩЕННЯ, `RelationSpecParser`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-TMPL-0422.relationSpecInvalid', N'en', N'Relation "{relationCode}": {detail}', 1)
    -- ru/kz — окремою порцією $Section у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d230 ── кінець секції ──
