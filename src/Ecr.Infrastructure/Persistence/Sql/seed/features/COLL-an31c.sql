-- COLL:an31c ── ru/kz AN-31 (L1-14); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REG-0409.externalKeyTakenElsewhere', N'ru', N'Идентификатор «{externalId}» источника «{dataSource}» уже связан с записью другого справочника.'),
    (N'err.ECR-REG-0409.externalKeyTakenElsewhere', N'kz', N'«{dataSource}» көзінің «{externalId}» идентификаторы басқа анықтамалықтың жазбасымен бұрыннан байланыстырылған.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:an31c ── кінець секції ──
-- COLL:an31c ── AN-31 (L1-14): 409 зовнішнього ключа без коду чужого запису; ru/kz — порцією COLL:an31c у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REG-0409.externalKeyTakenElsewhere', N'en', N'Identifier "{externalId}" of source "{dataSource}" is already linked to an entry of another registry.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:an31c ── кінець секції ──
