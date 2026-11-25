---
name: preread-worktree-may-show-mutation-1116
description: "A read-only pre-read of another session's live worktree can catch a mutation in progress; findings only against a pushed SHA"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T13:23:46.768Z
---

A pre-read of an author's local worktree is orientation only. Raise a finding only against a pushed SHA (or after asking the author whether a mutation script is running).

**Why:** 16.11 I flagged «PatchCellStylesHandler does not enforce Document.View» from PE's uncommitted tree. It was PE's rule-401 mutation script mid-step (it swaps the call, runs tests, restores). False alarm sent to PE and the coordinator, then retracted.

**How to apply:** when pre-reading `E:\ecr-wt\<lane>` before a push, use it to write the mutation list; if something looks wrong, ask the author first («is a mutation run active?») or re-read after a minute, and verify on the pushed SHA. See [[rule-401-mutate-every-branch-before-ready-1116]].
