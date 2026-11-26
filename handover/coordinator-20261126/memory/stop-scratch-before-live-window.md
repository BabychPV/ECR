---
name: stop-scratch-before-live-window
description: "W15m 10.11 — a leftover scratch stand ate memory, so the live start1 hit the 3072 MB guard after migration; stop all scratch stands before a live window"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-10T12:33:37.490Z
---

On 10.11 in W15m, my leftover rehearsal stand (app-W15m on EcrScratch_W15j) used the memory margin. The live start1, which ran after the migration, was refused by the memory guard (2991 MB < 3072 MB), so run-w15m exited 7. I fixed it forward in 3 min by stopping my stand and running `restart-w15m`; there was no RESTORE.

**Why:** after a migration the old app cannot start, so any refusal at start1 means downtime plus the 30-min rule.

**How to apply:**
- Before launching a live window, stop every scratch Ecr.Api/node of mine and check free memory ≥ 3.5 GB.
- Rebuild the live build dir with `-Replace` if it holds a rehearsal build.
- If start1 fails post-migration, fix the cause and run the remaining steps by hand in run-w15m order (`C:\EcrScratch\w15m\liveCont.ps1`).

**10.11, W15n rehearsal:** the root cause is that SQL Server max server memory is unlimited. CHECKDB and the backup in the window fill the buffer pool, which drops free memory below 3 GB AFTER the stop, even though preflight passed. The coordinator lowered the restart guard to 1536 MB (the app uses ~350 MB); the human was not asked about max server memory. Preflight-pre stays at ≥ 3 GB.

**W15n done 10.11 (621de9719, downtime 19 min).** Copy the next toolkit from `.sync-local\ready\w15n-drafts`, NOT w15m. It carries three fixes: the restart guard is 1536 MB, the rollback regex is `app-(w15[mn])` (shift it each window), and the seed parser rebuilds a changed tail of a multi-line tuple from the file.

See [[human-w15m-restore-fallback-1109]].
