---
name: human-quiz-fv76-fv121-1110
description: "10.11 quiz: ФВ-7.6 keep physical delete for unreferenced entities (close D-237); ФВ-12.1 manual recalc stays in the shared FIFO queue (close D-232)"
metadata:
  node_type: memory
  type: project
  originSessionId: e3c77d43-fb3b-47d3-b246-67858348a93f
  modified: 2026-11-10T13:22:41.364Z
---

On 10.11 the human answered two quiz questions in the coordinator's session.

- **ФВ-7.6 / D-237: «Залишити як є».** Unreferenced entities (validation rules, access rules, schedules, channels, drafts) are still deleted physically. The ФВ-7.6 text is updated to match the code.
- **ФВ-12.1 / D-232: «Спільна черга».** Manual recalculation keeps the shared FIFO queue with scheduled runs, with no separate quota or priority. The ФВ-12.1 text is updated to match.

**Why:** at current volumes (4–44 s per document period on a live copy), extra soft-delete or priority work isn't worth it.

**How to apply:** don't propose soft delete or a priority lane for these again unless volumes change. Audit-2 updates the ТЗ texts and the D-records.
