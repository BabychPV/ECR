---
name: f13-acceptance-script-1108
description: "From 08.11, F13 for UI or complex PRs = every line of the coordinator's acceptance script on a live-backup scratch, a screenshot per line, grep for old concepts; confusion = P2"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-08T10:21:51.158Z
---

F13 for UI or complex PRs (from 08.11):
1. The coordinator attaches an acceptance script built from the human's words. Check EVERY line on a scratch restore of the LIVE backup (not seed), with one screenshot per line. Any line of the human's request that is not done is FAIL.
2. Grep for leftover old concepts. After #771 removed approvals, the tooltip «changes need a separate approval» stayed, and the Month picker on Documents stayed although the human asked to remove it.
3. Anything a new user would find confusing is P2, not P3.

Server-only PRs keep the light F13 (diff + CI + 1 targeted check).

**Why:** the human, 08.11, relayed by the coordinator: «якість реалізації складних задач погана». My p205/p206 passes checked only the listed scenarios and did not look for leftovers.

**How to apply:** do not start a UI F13 without the script; if none is attached, ask the coordinator for it. Report a PASS/FAIL table line by line. Related: [[coordinator-is-final-tester]], [[human-live-windows-autonomous-1029]].
