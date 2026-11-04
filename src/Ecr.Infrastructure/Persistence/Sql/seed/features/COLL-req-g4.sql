-- COLL:req-g4 ── REQ-CLOSURE G4: область правила валідації (ФВ-5.3), причина публікації звіту (ФВ-14.7) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'err.ECR-REQ-0422.validationScope',       N'en', N'The rule scope must be 0 (cell), 1 (row), 2 (table) or 3 (document): any other value would save a rule that never runs.', 1),
    (N'err.ECR-REQ-0422.reportPublishReason',   N'en', N'A publication reason is required: an empty line explains nothing to whoever later asks why this report version was put into use.', 1),
    (N'reportDefs.publishReasonTitle',          N'en', N'Publish report version {version}', 1),
    (N'reportDefs.publishReason',               N'en', N'Publication reason', 1)
    -- ru/kz — окремою порцією `COLL:req-g4` у блоці I18N нижче.
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:req-g4 ── кінець секції ──
