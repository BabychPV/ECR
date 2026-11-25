---
name: au-rule-422-shared-memory-1117
description: "Rule 422 — the memory directory is shared between sessions; never overwrite another session's file (append a ✎ line only); name my own files with the au- prefix"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T22:48:19.367Z
---

The memory directory is shared by all sessions on this machine. Never replace another session's memory file; if a fact changes, append a ✎ line. My new files get the `au-` prefix so they cannot collide.

**Why:** night 16→17.11 my Write replaced the coordinator's `overnight-autonomy-1116.md` with my version (the Write tool said «updated», not «created» — the file already existed).

**How to apply:** before Write to `memory\<name>.md`, check it does not exist (Glob/Test-Path); if it exists and is not mine, use Edit to append a ✎ line instead. Prefix new files `au-…`.
