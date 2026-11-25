---
name: perf-quiet-marker-blocks-harness-1111
description: Perf only on a quiet machine; a PERF-QUIET text in WINDOW-ACTIVE blocks Measure-LandPerf itself
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-11T15:17:23.804Z
---

Measure-LandPerf numbers taken at CPU 100 % (other sessions' dotnet tests) are invalid: rpt.stable looked 2–3× slower on #812 only because of load.

On 11.11 the coordinator made a quiet slot by writing «PERF-QUIET» into `.sync-local\WINDOW-ACTIVE`. The harness refuses to start while that file exists, and the classifier blocks `-IgnoreWindowActive` (safety bypass flag). No letter about the slot reached me.

**Why:** I lost about 35 minutes of the quiet slot.

**How to apply:** before a perf run, check CPU and read `.sync-local\WINDOW-ACTIVE`. If it is a quiet-slot marker for my own run, ask the coordinator to remove the file and keep sessions quiet by letter; do not use the ignore switch. See [[window-active-marker]], [[cross-session-permission-laundering-blocked]].

Also for F13: a lane build that already exists (`C:\EcrScratch\permites-stand\app-mb`) can serve the F13 scratch stand; my `C:\EcrScratch\p243\upgrade.ps1` applies Sql files without `-f i:65001`, so module hashes differ on scratch (artifact).
