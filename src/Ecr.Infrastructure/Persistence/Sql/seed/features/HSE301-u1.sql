-- HSE301:U1 ── одиниця колонки шаблону й поля довідника звіряється з uom.Unit
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- (FK_ColumnDef_Unit / FK_RegField_Unit): описка в номері — 422, а не 500 на ключі.
    (N'err.ECR-TMPL-0422.unknownUnit', N'en', N'Column "{columnCode}": unit {unitId} does not exist in the unit catalog.', 1),
    (N'err.ECR-REG-0422.unknownUnit', N'en', N'Field "{fieldCode}": unit {unitId} does not exist in the unit catalog.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:U1 ── кінець секції
