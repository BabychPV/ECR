---
name: f13-ready-payload-line-1111
description: "F13 of any new read screen/endpoint — READY must carry a payload line (rows, KB on doc 11/Land, synthetic-doc number); missing = FAIL before perf"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-11T20:37:51.856Z
---

Coordinator retro 11.11: in F13 of any new read screen or endpoint, first check that the READY letter has a payload line — rows and KB measured on doc 11 (Land) plus the synthetic-doc number. If the line is missing, the verdict is FAIL before running perf.

**Why:** the year view (f4144158a) shipped a 9.17 MB response; nobody had measured payload size before perf, so the problem surfaced only in my timing run.

**How to apply:** make it the first check in every F13 of a read path (starting with lane/integ-calc-results-year-view); compare the stated KB with my own measurement. Related: [[f13-acceptance-script-1108]], [[human-perf-large-load-1110]].
