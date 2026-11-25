---
name: access-ready-deny-line-and-test-periods-1113
description: "Retro 381/382 (13.11): READY for access-path changes carries a «Deny mutation → red N/M» line, and AU runs the mutation; period-state tests use 2099xx or clock-relative periods"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-13T20:38:05.692Z
---

1. A READY letter for any change on an access path (AccessDecisionService, DocumentReadScope, CanReadColumn, SheetEditGate, owner-period reads) carries a line:
   `Deny mutation (for AU): <file:line, what to disable> → expected red: <tests>`, plus my own `revert of my fix → red N/M` where the change is mine.
   - I never disable an access check myself; the classifier blocks it, and see [[security-mutation-blocked-prove-red-before-fix]]. AU runs the mutation and fills in N/M.
   - The coordinator CONFIRMED (a) + (b) on 13.11 ~20:20Z for all sessions: (a) the named mutation goes in READY for AU to run; (b) my own revert → red N/M + a positive/Deny pair.
2. Tests that depend on period state (open, closed, current) use 2099xx test periods or periods relative to the clock, never near-real ones like 2026xx.

**Why:**
- On 13.11, L6 and P2.1 each cost an extra CI because the Deny proof was missing from READY.
- The test DocumentVersionMigrationTests.FromMonth hard-coded 202609. It went red on every CI once that month closed (the «time-bomb»).

**How to apply:** put both rules into subagent briefs for access-path or period-dependent tests. Check for them before writing READY.
