---
name: w15p-window-1111
description: "W15p 11.11 — live = main 3135a3138; rpt fingerprint check broke 2 attempts after a template publish; C: gate 15 GB"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-11T07:59:34.935Z
---

W15p (11.11, Audit-2): live = main 3135a3138 (#809 + #810), app from E:\ecr-app\app-W15p (junction F:\ecr-backup\land-ui\app-w15p). UiString 22608, migrations 114, K1 9D9AEBF3 unchanged. Kit: `.sync-local\ready\w15p-drafts`, launcher `C:\EcrScratch\w15p\goLive.ps1`.

**Why it matters for the next window:**
- If the previous window published a template or changed groups AFTER its last app start, the stored rpt fingerprints are 0|0 and start1 regenerates. The kit checks "fp == inputs 1|1" in TWO places: preflight (relaxed to INFO in w15p) and apply-sql (still strict). Attempt 2 failed there → auto rollback, ~10 min downtime. The rollback start itself fixes it to 1|1, so a rerun passes.
- The build refuses when C: has < 15 GB. On 11.11 I moved my old scratch folders to E:\EcrScratch-archive instead of deleting them. C:\EcrScratch\wt (22 GB) isn't mine.

**How to apply:** for W15q, copy w15p-drafts with names shifted. If a template was published since the last start, relax the apply-sql fp check too, or read the fp state first. Related: [[w15o-window-1111]], [[stop-scratch-before-live-window]].
