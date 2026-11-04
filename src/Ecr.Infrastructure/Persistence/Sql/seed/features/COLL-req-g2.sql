-- COLL:req-g2 ── Подання з попередженнями валідації: підтвердження подавача (ФВ-5.19, `SubmitSheetHandler`, `SheetActions`) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-SUB-4221.warningsNeedConfirmation', N'en', N'The sheet has {messageCount} validation warning(s). Review them and confirm to submit anyway.', 1),
    (N'workflow.submitWarningsTitle',           N'en', N'Submit with warnings?', 1),
    (N'workflow.submitWarningsHint',            N'en', N'Validation found warnings on this sheet. They do not block submission, but by submitting you confirm that you have reviewed them. The confirmation is recorded in the audit log.', 1),
    (N'workflow.submitAnyway',                  N'en', N'Submit anyway', 1),
    -- ФВ-1.11: розбіжність періоду на шляху запису комірок — `ECR-PRD-0422` (було `ECR-REQ-0422.periodMismatch`, `PatchCellsHandler.EnsurePeriodMatches`).
    (N'err.ECR-PRD-0422.periodMismatch',        N'en', N'The period {periodKey} in the request does not match period {expectedPeriodKey} of table instance {tableInstanceId}.', 1),
    -- ФВ-5.9: правило валідації бачить лише свою таблицю за поточний період (`RuleExpressionChecks`, збереження й публікація).
    (N'expr.ruleReferenceUnsupported',          N'en', N'A validation rule sees only its own table for the current period: the reference {reference} to another table, sheet or period is not supported.', 1)
    -- ru/kz — окремою порцією `COLL:req-g2` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:req-g2 ── кінець секції ──
