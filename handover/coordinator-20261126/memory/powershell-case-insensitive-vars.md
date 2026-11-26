---
name: powershell-case-insensitive-vars
description: "PowerShell variable names are case-insensitive — $h overwrites $H; window scripts broke after the first write (W15g, 06.11)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-06T06:36:40.441Z
---

In W15g (06.11) two window scripts broke on live because a local short name overwrote a config variable of the same name in another case:
- `$h = "<health>"` overwrote `$H = $W15g.Hse`, and the post-discard checks crashed;
- `$hl = Invoke-WebRequest` overwrote `$HL` (the log dir), and the tool outputs went to a wrong path.

**Why it slipped:** the dry run/plan mode returned before that code ran, so only the live run hit it.

**How to apply:**
- Before a window, scan the drafts for names that differ only in case (group `$name` matches by `ToLower()`).
- Give every write step a read-only mode that runs the code AFTER the write path (the post-checks), not just the guards.
- Also: never move tool output aside inside the pinned worktree; a dirty worktree FAILs the guard.

Related: [[human-standing-live-window-word-1029]].
