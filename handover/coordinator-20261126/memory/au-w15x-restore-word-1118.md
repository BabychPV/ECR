---
name: au-w15x-restore-word-1118
description: "Human 18.11 05:00Z (P-0118-14): RESTORE of live allowed ONLY in W15x and ONLY if the Land data step fails; test-w15x-landbuilder on live allowed"
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-18T05:11:53.839Z
---

Human quiz answer 18.11 05:00Z, relayed by the coordinator and mirrored as P-0118-14: «Так, RESTORE при збої».

**Scope:**
- Valid ONLY for window W15x, the yearly Land window on 18.11 in the evening.
- Valid ONLY if a Land data step fails (publish 1.0.19 / migrate doc 96 / recalc).
- Restore from the pre-window backup, then report.
- A dry run that is not clean still means STOP; it does not mean restore.

**Account:** test-w15x-landbuilder on live is allowed.
- Random password, never printed.
- Disable it after the window and list it in the report.
- No existing user's rights change.
- The script grants LandOwner + TemplateAdministrator. That is narrower than land-owner's full role set, but enough for the builder and migrate on the copy.

**Why:** Publishing the version and switching doc 96 cannot be undone by the toolkit.

**How to apply:**
- Never reuse this word for another window or for a code-step failure; for a code-step failure the toolkit's automatic code rollback applies.
- Related: [[human-standing-live-window-word-1029]], [[au-w15w-w15x-plan-1117]].
