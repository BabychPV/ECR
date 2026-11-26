---
name: overnight-autonomy-1109
description: Night 09→10.11 — human via coordinator «до ранку чекаю оновлений стенд без боргів»; F13s then W15m on final main by ~06:00
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-09T20:32:15.535Z
---

Human 09.11 evening (relayed by the coordinator before sleep): «до ранку чекаю оновлений стенд без боргів».

**Plan:**
1. F13 the #789 fix.
2. F13 Ship 2.
3. F13 the debt lane.
4. Merge.
5. **W15m** on the final main, done by ~06:00 local.

**Why:** the human wants live EcrLandUi updated (DPR01 + J01 + all lanes) by morning.

**How to apply:**
- W15m runs under the standing window word ([[human-live-windows-autonomous-1029]]).
- The RESTORE fallback only per [[human-w15m-restore-fallback-1109]] (after a failed 30-min fix-forward, only from bak-before).
- Re-rehearse only if the Sql or migration delta changed after rehearsal 2 (RC #789 cd7dfe9cc).
- Tell the coordinator when the window starts and ends; it does the live UI pass right after. After W15m comes the plan (u): LandRestricted re-assign verification (W15m-plan.md) and the UI pass.
- Before a scratch restore, recreate f13-p209 in the same step.
- Toolkit: `.sync-local/ready/w15m-drafts`.
- Coordinator after W15m:
  - my live UI pass of every changed screen ([[human-projects-page-and-live-ui-test-1108]]);
  - restore LandRestricted for land-owner via Security, which is also the live acceptance of fix (u);
  - a morning report to the human: what is live, the pass results, anything left;
  - then moderate mode.
- A P3 that would block the window goes into the report, not into another lane.
