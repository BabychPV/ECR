---
name: aa-fast-lanes-batch-ci-1117
description: P-0117-5 (human 17.11) — own lanes are fast lane/aa/<task>; coordinator batches READY lanes into lane/integ-batch-* for one full CI/F13/RC
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-17T09:06:20.484Z
---

P-0117-5, the human 17.11 09:10Z: «важкі прогони — на пачках, де багато змін». Via the coordinator:
- My lanes are FAST: `lane/aa/<task>`, not `integ-*`. Local checks: build + Architecture + affected-class tests + affected e2e specs via grep. No full CI per lane, no full vitest per lane.
- When a lane is READY, send the coordinator the SHA. The coordinator cherry-picks several READY lanes onto fresh main as `lane/integ-batch-<date>-N`. That batch gets ONE full CI, ONE AU F13 and ONE RC.
- 20× and mutations only for the risk class (concurrency, security, data, contracts, guards).

**Why:** full CI and F13 per lane cost hours and runner queues.

**How to apply:** push READY work to `lane/aa/<task>` and brief subagents accordingly. Related: [[aa-rules-428-429-1117]], [[speed-rules-ci-process-1012]].
