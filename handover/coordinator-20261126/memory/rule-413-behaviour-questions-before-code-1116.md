---
name: rule-413-behaviour-questions-before-code-1116
description: Retro 413 (16.11) — for big lanes (>1000 lines or a new access/recalc rule) AU reads the plan/design BEFORE the code and sends behaviour questions to the coordinator
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T16:49:43.955Z
---

For any big lane (> ~1000 lines, or a new access / recalculation rule), read the PLAN / design file BEFORE coding starts and send the coordinator a short list of behaviour questions; the coordinator puts them to the human before the developer codes.

**Why:** 16.11 my B79 pre-read found 3 behaviour questions (year anchor vs Approved months, cross-sheet dependents, approver edit clearing the mark) only after 28 commits — a night lost.

**How to apply:** between F13s, read-only: `.sync-local\RESUME-*-plan.md`, design reports, the PLAN row. Questions one-word-answerable with a default. Typical axes: blast radius across months/sheets/documents, Approved data never rewritten, races (which lock, re-check under lock), disclosure to non-readers (D-6), who can switch flags and is it audited, live data facts checked read-only. First done for B80 and B151 on 16.11. Related: [[rule-410-mutation-counts-only-if-builds-1116]].
