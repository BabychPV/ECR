---
name: au-rule-423-test-count-proof-1117
description: Rule 423 — a mutation result or an N× claim counts only with the executed test count shown (Total ≥ 1); Cyrillic --filter via cmd.exe matched zero tests
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T23:52:24.260Z
---

Every F13 mutation row and every N×-run claim must show the executed test count (Total ≥ 1). A `dotnet test --filter` with Cyrillic method names passed through cmd.exe matched ZERO tests (PE, 16.11) — the output looks green, the proof is vacuous.

**Why:** a filter that matches nothing proves nothing; «20/20» or «red/green» without Total can be empty.

**How to apply:** filter by ASCII class names (`FullyQualifiedName~ClassName`), log the `Passed!/Failed! … Total: N` line per run (my scripts already do), for vitest check «Test Files N passed» with N ≥ 1; ask authors for counts in their READY letters. Tell subagents in the brief. Related: [[rule-410-mutation-counts-only-if-builds-1116]].
