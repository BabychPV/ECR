-- COLL:calc-limits ── Бюджет комірок входу прив'язки методології (ФВ-9.8, D-205, `CalculationOrchestrator`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-CALC-4222',                      N'en', N'Calculation input is too large', 1),
    (N'err.ECR-CALC-4222.inputCellsOverBudget', N'en', N'Methodology {code} bound to table {tableInstanceId} has {cells} input cells, more than the limit of {limit} per binding. Check the data and the binding rule, or raise Calculations:MaxInputCellsPerBinding.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:calc-limits ── кінець секції ──
