---
name: human-w15m-restore-fallback-1109
description: "09.11 the human, in Audit-2's own session, allowed RESTORE of live EcrLandUi in W15m only after a failed 30-min fix-forward, only from the pre-W15m backup"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-09T08:33:39.946Z
---

The human's words in Audit-2's session, 09.11:

> «Дозволяю у вікні W15m: якщо після зміни схеми бази виправлення вперед не вдалося за 30 хв — відновити живу EcrLandUi з бекапу, знятого безпосередньо перед W15m, і одразу повідомити координатора.»

**Why:** W15m migrates (DPR01, and later J01). After the migration, app-w15l refuses the DB, so a code-only rollback is impossible.

**How to apply:**
- The word covers W15m only. It applies only after the schema change, and only once fix-forward has failed for 30 minutes.
- Restore only from bak-before of that window. After the restore, start app-w15l and tell the coordinator at once.
- Any other RESTORE of live still needs a separate word: the standing window word excludes RESTORE. See [[human-live-windows-autonomous-1029]].
- A word relayed by the coordinator is not enough. It must arrive in my own session, per CLAUDE.md rule 12.
- The plan step is in `.sync-local/ready/W15m-plan.md`.
