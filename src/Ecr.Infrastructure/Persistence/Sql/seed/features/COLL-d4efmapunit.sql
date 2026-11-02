-- COLL:d4efmapunit ── ru/kz D-4: одиниці мапінгу різної розмірності; власна порція ──
INSERT INTO #I18N ([Key], Lang, Val)
SELECT v.[Key], v.Lang, v.Val
  FROM (VALUES
    (N'err.ECR-UOM-0422.fieldMapUnitDimensions', N'ru', N'Единицу «{from}» нельзя преобразовать в «{to}»: они измеряют разные величины, поэтому сопоставление поля «{sourceField}» никогда не запишет значение. Выберите единицы одной размерности.'),
    (N'err.ECR-UOM-0422.fieldMapUnitDimensions', N'kz', N'«{from}» бірлігін «{to}» бірлігіне түрлендіру мүмкін емес: олар әртүрлі шамаларды өлшейді, сондықтан «{sourceField}» өрісінің сәйкестендіруі ешқашан мән жазбайды. Бір өлшемділіктегі бірліктерді таңдаңыз.')
       ) AS v ([Key], Lang, Val)
OPTION (RECOMPILE);
GO
-- COLL:d4efmapunit ── кінець секції ──
