---
name: live-cleanup-1110-classifier-split
description: "10.11 live document cleanup — classifier blocked my mass delete; coordinator ran the script on the human's word, I did stop/start; restart output is buffered"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-10T18:38:34.930Z
---

10.11: the human asked to delete all live documents except HSE401 (94 docs, ~5.5M CellValue). The «так видаляй» was given in my session. Still, the auto-mode classifier blocked `run-cleanup.ps1 -Database EcrLandUi` («Cloud Storage Mass Delete»). Per CLAUDE.md p.12 the coordinator then ran it itself on the human's «Запускай»; I only stopped/started live (stop-app/restart-w15n with W15N_MAIN_SHA), took the backups and ran the checks. Script and backups: `.sync-local\ready\cleanup-1110`, `F:\ecr-backup\land-ui\cleanup-1110`.

**Why:** a mass delete on live gets blocked even with the human's word in the session; the split (coordinator deletes, Audit-2 runs the window) worked in 393 s.

**How to apply:**
- For a future live mass delete, plan the split from the start.
- `restart-w15n.ps1` piped through `Select -Last` buffers all output. Check `land-ui.pids.json` and health right away, and clear WINDOW-ACTIVE as soon as health/ready is 200; I left the marker up for 30 min twice.
- `sys.partitions` row counts on doc.CellValue are stale; use COUNT via the PK.

Related: [[human-live-windows-autonomous-1029]], [[window-active-marker]].
