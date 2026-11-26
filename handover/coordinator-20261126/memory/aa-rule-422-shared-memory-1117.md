---
name: aa-rule-422-shared-memory-1117
description: "Rule 422 — memory dir shared by all sessions; never overwrite another session's file, append ✎ only; new files get role prefix aa-"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-16T22:48:17.827Z
---

Rule 422 (coordinator, 17.11, all sessions): the memory directory is shared between sessions. Never overwrite another session's memory file — only append a ✎ line. Name my own new files with the role prefix `aa-…`.

**Why:** several sessions write the same directory; overwrites lose other sessions' facts.

**How to apply:** before Write to an existing memory file — use Edit to append a ✎ line instead; new file names start with `aa-`. Related: [[session-ids-1116]].
