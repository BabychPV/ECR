-- HSE301:F5 ── прив'язка PI за вікном рядка: доменні відмови (FEATURE-HSE301-VIEW §4.4, D-171).
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- ⚠ windowColumnsNotDate, targetNotDecimal, selectorNotInTable заведено ТУТ, а не в F8:
    -- кидає їх уже домен F5. F8 їх не дублює — повтор ключа ламає MERGE.
    (N'err.ECR-INT-0422.windowColumnsNotDate', N'en', N'The row window is taken from Date columns: "{startColumn}" and "{endColumn}" must both be of type Date.', 1),
    (N'err.ECR-INT-0422.targetNotDecimal', N'en', N'The folded value can only be written to a Decimal column; "{targetColumn}" is {dataType}.', 1),
    (N'err.ECR-INT-0422.windowColumnNotInTable', N'en', N'Window column "{column}" belongs to a different table than target column "{targetColumn}".', 1),
    (N'err.ECR-INT-0422.selectorNotInTable', N'en', N'Selector column "{selectorColumn}" belongs to a different table than target column "{targetColumn}".', 1),
    (N'err.ECR-INT-0422.windowColumnsSame', N'en', N'The window starts and ends in the same column "{column}": such a window is always empty.', 1),
    (N'err.ECR-INT-0422.rowWindowPolicyOutOfRange', N'en', N'Binding parameter {parameter} = {value} is out of range.', 1),
    (N'err.ECR-INT-0422.rowWindowSelectorWithoutColumn', N'en', N'The binding has no selector column, so selector value "{selectorValue}" would never match a row.', 1),
    (N'err.ECR-INT-0409.rowWindowSelectorTaken', N'en', N'This selector value already has a source in the binding.', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- HSE301:F5 ── кінець секції
