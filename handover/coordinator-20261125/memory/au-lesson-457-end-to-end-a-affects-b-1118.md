---
name: au-lesson-457-end-to-end-a-affects-b-1118
description: "Lesson 457 (coordinator 18.11): every «A affects B» feature needs an end-to-end scenario on a stand/copy plus a check of shipped config defaults, in F13 and window post-checks"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-18T20:00:43.188Z
---

For every feature of the form «A affects B», F13 and window post-checks must include an end-to-end scenario on a stand or copy:
- change A through the normal user path (UI/API edit);
- then confirm B updates with no manual step.

Also check the shipped config defaults (appsettings.json, live-env) against the feature's intent.

**Why:** B80 N1 passed F13 and the W15x window but did not work on live:
- appsettings has Hse400RecalculateLandDependents=false;
- a cell edit in HSE400 triggers nothing;
- the Land template formulas are not registered as dependents.

Mutations and unit tests proved the pieces, not the chain. This is now B179.

**How to apply:**
- In each F13 READY review, list the A→B chains in the batch. For each, run one real edit on a copy and observe B.
- grep the defaults in `src/Ecr.Api/appsettings.json` and `F:\ecr-backup\land-ui\live-env.json` for the feature flags.
- Put both the chain result and the flag values in the report.

Related: [[au-w15x-restore-word-1118]], [[rule-401-mutate-every-branch-before-ready-1116]].
