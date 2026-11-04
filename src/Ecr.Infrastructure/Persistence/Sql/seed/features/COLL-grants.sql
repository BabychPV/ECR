-- COLL:grants ── Гранти ролі: вибір ресурсу за назвою, підписи переліків, незбережена чернетка (аудит U6) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    -- ⚠ Англійські підписи переліків навмисно збігаються зі значеннями: так їх
    -- і досі шукає e2e (`security.spec.ts`, опція `Read`/`Write`).
    (N'enum.resourceKind.Project',              N'en', N'Project', 1),
    (N'enum.resourceKind.Sheet',                N'en', N'Sheet', 1),
    (N'enum.resourceKind.Table',                N'en', N'Table', 1),
    (N'enum.resourceKind.Column',               N'en', N'Column', 1),
    (N'enum.resourceKind.Registry',             N'en', N'Registry', 1),
    (N'enum.grantLevel.Read',                   N'en', N'Read', 1),
    (N'enum.grantLevel.Write',                  N'en', N'Write', 1),
    (N'enum.grantLevel.Submit',                 N'en', N'Submit', 1),
    (N'enum.grantLevel.Approve',                N'en', N'Approve', 1),
    (N'enum.grantLevel.Manage',                 N'en', N'Manage', 1),
    (N'grants.target',                          N'en', N'Resource', 1),
    (N'grants.pickerProject',                   N'en', N'Project', 1),
    (N'grants.pickerRegistry',                  N'en', N'Registry', 1),
    (N'grants.pickerTemplate',                  N'en', N'Template', 1),
    (N'grants.pickerVersion',                   N'en', N'Version', 1),
    (N'grants.pickerSheet',                     N'en', N'Sheet', 1),
    (N'grants.pickerTable',                     N'en', N'Table', 1),
    (N'grants.pickerColumn',                    N'en', N'Column', 1),
    (N'grants.pickerNothingFound',              N'en', N'Nothing found', 1),
    (N'grants.pickerLoadFailed',                N'en', N'Could not load the list', 1),
    (N'grants.projectsScopeHint',               N'en', N'Only projects you have access to are listed. Sheets, tables and columns are picked in the template version they belong to.', 1),
    (N'grants.unsaved',                         N'en', N'Unsaved changes', 1),
    (N'grants.pickResourceFirst',               N'en', N'Unsaved changes: pick a resource in every row before saving.', 1),
    (N'grants.discardTitle',                    N'en', N'Discard unsaved grant changes of role {role}?', 1),
    (N'grants.discardText',                     N'en', N'The grant changes of this role have not been saved. Switching the role discards them.', 1),
    (N'grants.discardVerb',                     N'en', N'Discard changes', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:grants ── кінець секції ──
