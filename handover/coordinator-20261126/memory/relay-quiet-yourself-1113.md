---
name: relay-quiet-yourself-1113
description: "Retro 371 (13.11): for a measurement, send «quiet» and «quiet over» to AN/AA/AU yourself, not via the coordinator (its letters are capped)"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-13T14:42:30.525Z
---

When I need a quiet slot for a measurement, I send «quiet» and later «quiet over» to every other session myself: AN, AA, AU. I don't route it through the coordinator.

**Why:** on 13.11 my R1 «after» run hit 100 % CPU because AN's tests were still running: my «quiet» sat queued at the coordinator, whose letters are capped. Coordinator retro 371.

**How to apply:**
- Use ListAgents to get the current session names.
- Send «quiet» to each of them, wait a minute, and check that CPU is below 40 % before measuring.
- Send «quiet over» to the same list afterwards.
- Discard any run made at a high CPU reading.
