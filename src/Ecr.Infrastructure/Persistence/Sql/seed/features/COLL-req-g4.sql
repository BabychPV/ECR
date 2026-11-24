-- COLL:req-g4 ── REQ-CLOSURE G4: область правила валідації (ФВ-5.3), причина публікації звіту (ФВ-14.7) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.validationScope',       N'en', N'The rule scope must be 0 (cell), 1 (row), 2 (table) or 3 (document): any other value would save a rule that never runs.', 1)
    -- ru/kz — окремою порцією `COLL:req-g4` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:req-g4 ── кінець секції ──
