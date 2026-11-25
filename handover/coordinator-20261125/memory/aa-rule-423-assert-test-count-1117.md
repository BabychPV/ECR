---
name: aa-rule-423-assert-test-count-1117
description: Rule 423 — every 20× repeat or mutation loop must assert executed test count ≥1; zero-match filter = vacuous pass
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-16T23:52:19.867Z
---

Rule 423 (coordinator, 17.11, all sessions): a Cyrillic test filter passed through cmd.exe matched ZERO tests and «20/20» was vacuous. Every repeat or mutation loop must assert the executed count (dotnet Total ≥ 1 or exact; vitest «Tests N passed», N ≥ 1).

**Why:** a zero-match filter exits 0 and looks green.

**How to apply:** in loops, parse the count and fail on 0; write "N/20 (each M tests)" in commits; mutation tables list the executed count. Brief subagents with it. Related: [[timing-tests-run-20-times-before-push]], [[aa-rule-422-shared-memory-1117]].
