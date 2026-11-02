-- COLL:d3efmap ── ru/kz D-3: дубль мапінгу пари (сутність, поле); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-INT-0409.fieldMapDuplicate', N'ru', N'Для сущности источника {sourceEntityId} сопоставление поля «{sourceField}» уже есть: измените его, а не создавайте второе.'),
    (N'err.ECR-INT-0409.fieldMapDuplicate', N'kz', N'{sourceEntityId} көз нысаны үшін «{sourceField}» өрісінің сәйкестендіруі бұрыннан бар: екіншісін жасамай, оны өзгертіңіз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d3efmap ── кінець секції ──
-- COLL:d3efmap ── D-3 приймальної №8: другий мапінг тієї ж пари (сутність, поле) — 409, не 500; ru/kz — порцією COLL:d3efmap у блоці I18N нижче ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-INT-0409.fieldMapDuplicate', N'en', N'Source entity {sourceEntityId} already has a mapping for field "{sourceField}": edit it instead of creating a second one.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:d3efmap ── кінець секції ──
