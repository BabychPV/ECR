---
name: rule-410-mutation-counts-only-if-builds-1116
description: Retro 410 (16.11) — a mutation counts only if the build passes AND the expected test goes red; mark «build OK» per mutation in F13 verdicts
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T14:48:25.015Z
---

Rule 410 (coordinator retro 16.11, applies to AU's F13s): a mutation proves something only when the mutated code COMPILES and the expected test then goes red. A compile error (e.g. CS8629 nullable-as-error, unreachable code, unused variable) is not a red. Mark «build OK» per mutation in every verdict table.

**Why:** 16.11 three of my B144 own-template mutations failed with CS8629; I caught it and reran in a compilable form (`TemplateId == null || d.Id > 0`), but the rule makes the check explicit for everyone. PE hit the same with «unread parameter / unreachable code».

**How to apply:** my mutation scripts log `error CS` in the summary line — treat any such line as «NOT A MUTATION», rewrite and rerun; in the verdict, list each mutation with «build OK + red test». Related: [[rule-401-mutate-every-branch-before-ready-1116]]; my own rule 411-AU = [[preread-worktree-may-show-mutation-1116]].
