---
name: window-active-marker
description: "02.11 coordinator: create .sync-local/WINDOW-ACTIVE ~5 min before a live window's CPU check, delete after postcheck — other sessions pause heavy builds"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-02T12:59:15.631Z
---

Coordinator 2026-11-02: before every live window on EcrLandUi, create `H:\ECR\.sync-local\WINDOW-ACTIVE` (one line: window id + start time) ~5 min before the CPU guard check; delete it after postcheck. Sessions Analiz and Permites pause heavy local builds while it exists.

**Why:** W14z attempt 1 was refused by the CPU guard (84.8 % from other sessions' builds) → rollback, 7 min downtime instead of ~3.

**How to apply:** put create/delete of the marker into every window agent prompt (also delete on FAIL/rollback). Related: [[human-standing-live-window-word-1029]].
