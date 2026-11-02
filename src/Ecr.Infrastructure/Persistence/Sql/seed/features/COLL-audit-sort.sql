-- COLL:audit-sort ── вкладка «Workflow events», сортування заголовків і підпис сутності (ru/kz нижче) ──
INSERT INTO #UiEn ([Key], Lang, Val, Scope)
SELECT v.[Key], v.Lang, v.Val, v.Scope
  FROM (VALUES
    (N'audit.viewWorkflow', N'en', N'Workflow events', 1),
    (N'audit.workflowEmpty', N'en', N'No workflow events in this window.', 1),
    (N'audit.action', N'en', N'Action', 1),
    (N'audit.comment', N'en', N'Comment', 1),
    (N'audit.sheet', N'en', N'Sheet', 1),
    (N'audit.statusChange', N'en', N'Status', 1),
    (N'audit.systemActor', N'en', N'System', 1),
    (N'audit.sortBy', N'en', N'Sort by {column}', 1),
    (N'audit.sortedAsc', N'en', N'sorted ascending', 1),
    (N'audit.sortedDesc', N'en', N'sorted descending', 1),
    (N'audit.workflowAction.Submit', N'en', N'Submitted', 1),
    (N'audit.workflowAction.Approve', N'en', N'Approved', 1),
    (N'audit.workflowAction.ApproveStep', N'en', N'Step approved', 1),
    (N'audit.workflowAction.Reject', N'en', N'Rejected', 1),
    (N'audit.workflowAction.Reopen', N'en', N'Returned for rework', 1),
    (N'audit.workflowAction.Recall', N'en', N'Recalled', 1)
       ) AS v ([Key], Lang, Val, Scope)
OPTION (RECOMPILE);
GO
-- COLL:audit-sort ── кінець секції ──
