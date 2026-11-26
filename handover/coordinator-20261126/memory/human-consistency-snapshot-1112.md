---
name: human-consistency-snapshot-1112
description: "12.11 human: the consistency check is a point-in-time snapshot — show current problems only; findings that disappeared are deleted, not kept as resolved"
metadata:
  node_type: memory
  type: project
  originSessionId: 63e87d11-16e6-467b-afcb-ccd5cf06c240
  modified: 2026-11-12T13:25:26.175Z
---

The human, 12.11, after 260 stale warnings were found on live: «головне щоб система дійсно показувала проблеми і коли вони зникали прибирала повідомлення (видаляла), консистентність це як перевірка в момент часу».

**How to apply:** aud.ConsistencyIssue has snapshot semantics. Each run replaces the findings of every rule it fully evaluated: findings that are no longer true are DELETED, still-true ones stay, new ones are added. A skipped or capped rule keeps its rows. The UI shows the current set plus the last-check time, with no «resolved» history. AN implements this in lane/integ-consistency-autoresolve.
