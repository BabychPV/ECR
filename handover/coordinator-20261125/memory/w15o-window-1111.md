---
name: w15o-window-1111
description: "W15o 11.11 facts — live runs from E: via junction, K1 re-baselined after cleanup, smoke fixed for no Land docs; next toolkit = copy w15o-drafts"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-11T02:26:45.892Z
---

W15o (11.11, Audit-2): live = main ffd2f3cf3 (#798–#808); LAND_UI 1.0.14.0 has month bands (989 groups); new LAND_UI 1.0.15.0 = id 17, published (2,100 month-bound, 1,085 groups).

**Why it matters for the next window:**
- F: was full (someone's EcrCl10Scratch ~19 GB). By coordinator decision the app build and window logs/backups live on E:. `F:\ecr-backup\land-ui\app-W15o` is a JUNCTION → `E:\ecr-app\app-W15o`. The F: gate is 3 GB (build-w15o / preflight). My old `k1-window15i` is copied to `E:\EcrBackup\archive-F\` (verified); deleting the F: original was blocked by the classifier and waits for the human.
- After the 10.11 cleanup live has no Land documents: K1 was re-baselined (`C:\EcrScratch\w15o\k1\pre-W15o-20261111-025317.cells.tsv`). http-smoke falls back to any document. Run with -SkipRecalc (LAND-PROD-4 is gone).
- Attempt 1 rolled back automatically on the smoke bug. The rollback restarted the OLD app, which re-seeded the 3 removed keys. Attempt 2 then needed `-SeedDone`.
- Template steps (bands, Build-LandTemplate) run under their own WINDOW-ACTIVE after the deploy. Scripts: `C:\EcrScratch\w15o\live-bands.ps1`, `live-build.ps1` (cred file read in place, made read-only).

**How to apply:** for W15p, copy `w15o-drafts` with names shifted (`C:\EcrScratch\w15o\mk-w15o.ps1` pattern; fix the `app-w15[..]` regexes by hand). Update LiveSha / PrevBak / K1Baseline / UiString facts. Keep Work/Logs/BakDir on E: while F: is short. Related: [[stop-scratch-before-live-window]], [[live-cleanup-1110-classifier-split]].
