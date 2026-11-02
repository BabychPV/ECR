-- HSE301:A3a ── область формули, проміжні результати (FEATURE-HSE301-VIEW §6.1, §7.1; D-175, D-176):
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- публікація відхиляє видиму формулу без одиниці, Row-формулу з посиланням «лише для речовини»
    -- і вихід «раз на рядок» із формули речовини.
    (N'err.ECR-CALC-0422.visibleFormulaNoUnit', N'en', N'Formula "{formula}" is shown as an intermediate result but has no result unit. A result without a unit cannot be stored: set the unit or clear "visible".', 1),
    (N'err.ECR-CALC-0422.rowScopeReferencesSubstance', N'en', N'Formula "{formula}" is calculated once per row, but it uses {reference}, which has a value only for a substance. Make "{formula}" a per-substance formula or remove the reference.', 1),
    (N'err.ECR-CALC-0422.rowOutputFromSubstanceFormula', N'en', N'Output "{output}" is written once per row, but its formula is calculated for every substance, so it is unclear which value belongs to the row. Make the formula per-row or write the output per substance.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:A3a ── кінець секції
