---
name: human-flare-ef-schedule-1106
description: Human 06.11 Q-2/Q-3 — flare EventFrame collection frequency is a UI setting (default hourly); thousands of EF per month must be handled
metadata:
  node_type: memory
  type: project
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-11-06T13:17:40.425Z
---

Human 2026-11-06 (via coordinator):
- Q-2: «це має бути параметр налаштування в UI» — the flare EventFrame collection frequency is editable in the UI (ext.CollectionSchedule of that source), default hourly.
- Q-3: «Тисячі» EventFrames per month — the AN-4 full reconcile and limits/health thresholds must handle thousands per period (portions, no timeouts).

**Why:** flare EF volume and cadence are customer facts; don't hard-code them.
**How to apply:** don't re-ask; size EF sync tests/limits for thousands per period. Related: [[hse301-priority-eventframe]].
