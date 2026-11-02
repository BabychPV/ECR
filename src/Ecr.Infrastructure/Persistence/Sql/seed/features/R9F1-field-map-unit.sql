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

