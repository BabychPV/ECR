-- R9F1:fieldMapUnit ── ru/kz: ціль мапінгу у колонку з одиницею — одиниця колонки (F1-02); власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-REQ-0422.fieldMapTargetNotColumnUnit', N'ru', N'Целевая единица сопоставления должна быть единицей колонки: ячейка хранит значение именно в ней.'),
    (N'err.ECR-REQ-0422.fieldMapTargetNotColumnUnit', N'kz', N'Сәйкестендірудің мақсатты бірлігі бағанның бірлігі болуы керек: ұяшық мәнді дәл сол бірлікте сақтайды.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- R9F1:fieldMapUnit ── кінець секції ──

-- R9F1:fieldMapUnit ── F1-02: ціль мапінгу у колонку з одиницею — одиниця колонки (`CreateEntityFieldMapHandler.ApplyUnitsAsync`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.fieldMapTargetNotColumnUnit', N'en', N'The target unit of the mapping must be the unit of the column: the cell stores its value in that unit.', 1)
    -- ru/kz — окремою порцією `R9F1:fieldMapUnit` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- R9F1:fieldMapUnit ── кінець секції ──
