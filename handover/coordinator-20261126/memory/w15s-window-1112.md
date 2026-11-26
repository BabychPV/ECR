---
name: w15s-window-1112
description: W15s live window 12.11 — live now app-W15s 8c9b365ff; toolkit w15s-drafts; how it was cloned from w15r
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-12T20:42:19.322Z
---

W15s done 12.11 22:15 local: live EcrLandUi = app-W15s main 8c9b365ff (115 migrations TL01, UiString 22659), api/web PIDs in F:\ecr-backup\land-ui\land-ui.pids.json. K1 cells baseline unchanged 9D9AEBF3…. Backups E:\EcrBackup\land-ui\w15s\bak (after = …-after-W15s-20261112-2214.bak, F855981B…).

Toolkit .sync-local\ready\w15s-drafts = copy of w15r-drafts with placeholder renames (W15r→W15s, W15q→W15r; do NOT chain plain Replace — env names W15R_* broke into W15sX on the first try). Then edit in w15s-common: TargetDefaultSha, LiveSha/PrevShort, MustContain, PreMigrations/LastMigration/PrevLastMigration, ExpectedUiStringBefore, PrevBak+sha, MigCols/NewCols, ProcRx `[rs]` (+ run-w15s Get-RunningAppDir regex), K1BaselineFile. Rehearsal: restore after-backup into EcrScratch_W15j (name hard-coded), copy live app dir to C:\EcrScratch\w15s\land-ui, start prev app with restart -Target w15r, then run -Scratch -SkipRecalc -SkipK1Cells.

**Why:** the next window (W15t) repeats this; the live window needs green main CI on the exact SHA (main runs get cancelled by later pushes — check `gh run view`).
**How to apply:** clone w15s-drafts → w15t the same way; preflight fails at CPU ≥ 60 %, so use a waitcpu wrapper. Related: [[w15q-window-1111]], [[human-live-windows-autonomous-1029]].
