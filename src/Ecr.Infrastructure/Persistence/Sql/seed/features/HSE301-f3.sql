-- HSE301:F3 ── конверсія одиниць на межі після згортки (FEATURE-HSE301-VIEW §4.2, D-173, ФВ-16.10):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- несумісна одиниця чи інтеграл без одиниць — відмова, а не число в одиниці джерела.
    (N'err.ECR-UOM-0422.integralUnitsUndeclared', N'en', N'A time integral can only be written with both the source and the target unit declared on the mapping; otherwise the value would stay in unit × seconds.', 1),
    (N'err.ECR-UOM-0422.integralSourceNotRate', N'en', N'A time integral needs a rate unit (quantity per time) at the source; "{code}" is not one.', 1),
    (N'err.ECR-UOM-0422.boundaryConversionFailed', N'en', N'{count} mapped values could not be converted to their target unit and were not written: {fields}', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:F3 ── кінець секції
