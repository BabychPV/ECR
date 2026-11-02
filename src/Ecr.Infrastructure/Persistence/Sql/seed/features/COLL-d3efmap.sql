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
