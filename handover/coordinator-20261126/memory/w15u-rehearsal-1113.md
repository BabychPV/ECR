---
name: w15u-rehearsal-1113
description: "W15u rehearsal 13.11 PASS on 840be2d5c + HSE400 steps 3–8 + B55 on scratch; toolkit w15u-drafts fixes (rebuilt index, F: scratch root, HSE400 flag env)"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-13T13:25:00.410Z
---

W15u toolkit = `.sync-local\ready\w15u-drafts` (clone of w15t; LiveSha e8a5c9ffc, PrevBak after-W15t 2843AA19…). Report `.sync-local\reports\W15u-rehearsal.md`.
Fixes: `RebuiltIdxs`/`RebuiltIdxShape` (#862 CRY01 rebuilds IX_CalculationResult_DocRun — allowed only with the exact new shape); scratch root F:\EcrScratch\w15u (C: < 15 GB; build/preflight gate C: only for live); restart-w15u opt-in `W15U_HSE400_FLAG=1` → env `ECR_Calculations__Hse400CompositionFromDocument=true` + launch-flags proof.
Content scripts: F:\EcrScratch\w15u\hse400-steps.ps1 (steps 3–5 from C:\EcrScratch\w15k-build), recalc-proj.mjs (project recalc, avoids the 403 on doc-96 whole recalc), b55.mjs (clone V5→V6 'V6', PUT PARITY_1 tol 1e-12, publish effectiveFrom 2024-01-06).

**Why:** the live W15u window (GO ≈ 14.11 09:00Z) repeats this; run 1 failed only on the index rule.
**How to apply:** scratch run needs the PREVIOUS app already running on 5196 (start via restart-w15u -Target w15t) and the prev app copied into the scratch root, else preflight fails; wrapper needs 2×30 s < 45 % CPU with retries on a preflight CPU abort. Related: [[w15t-window-1113]], [[scratch-api-port-owner-check]].
