---
name: batch-lanes-1112
description: "From 12.11 ~20:00Z fixes ship in batches, 3–6 of one area in one lane/integ-<session>-batch-<n>; push once, one CI, one READY"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-12T19:47:51.903Z
---

Human rule (12.11 ~20:00Z, passed on by the coordinator, written at the top of PLAN.md): group 3–6 fixes from one area into ONE lane, `lane/integ-an-batch-<n>`.
- One commit plus one red test per fix.
- Push only when the batch is complete → ONE full CI → one READY listing the PLAN items → one RC → one F13.
- Never in a batch, always separate: EF migration, contract, `.github/**`, lock files.

**Why:** each single-fix lane cost a full CI, an RC and an F13. Runners were saturated and queues grew.

**How to apply:** when briefing agents, give a batch of PLAN items with one worktree and branch. Agents commit each fix separately and push only at the end. Lanes pushed before the rule (B10/B12/B21 on 12.11) were finished as they were.

Related: [[single-ordered-plan-1112]], [[speed-rules-ci-process-1012]].
