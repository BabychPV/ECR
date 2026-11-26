---
name: no-full-ci-for-mutations-1113
description: "F13 mutation proofs must never be pushed as branches that run the full ci.yml (retro rule 359, 13.11)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-13T08:38:19.182Z
---

Never push throwaway mutation branches (e.g. lane/aa-f13-*-mut*) that trigger the full ci.yml. Run a mutation locally on ONE test class, or by workflow_dispatch of only the affected test project. If heavy local runs are on hold (quiet slot / live window), the mutation waits for «quiet over».

**Why:** 13.11 my two mutation branches for F13 #872 were 2×25 CI jobs; they held the runner queue and B71 + the docs CI waited 1.5 h (coordinator retro rule 359).

**How to apply:** in F13/mutation briefs to subagents, forbid pushing mutation branches; say «local one class, or defer». Related: [[full-ci-on-final-tip-1110]], [[speed-rules-ci-process-1012]].
