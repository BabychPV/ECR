---
name: perf-quiet-slot-marker-clash
description: "11.11: WINDOW-ACTIVE as a perf quiet-slot marker blocks the perf harness; removing it lets sessions resume builds — use letters + a separate marker"
metadata:
  type: feedback
---

On 11.11 I set .sync-local\WINDOW-ACTIVE (text PERF-QUIET) to give AU a quiet slot for perf. AU's Measure-LandPerf refuses to run while that file exists (classifier blocked -IgnoreWindowActive). When I removed it, AN and PE took the missing marker as «quiet over» and started dotnet test → CPU 100 %, perf dirty again.

**Why:** the marker means «live window», and both the harness and the sessions key off its presence.

**How to apply:** for a perf quiet slot, do NOT use WINDOW-ACTIVE. Send letters to AN and PE («no local build/test until I write quiet over»), check CPU < 50 % before telling AU «go», and end it with an explicit «quiet over» letter. Related: [[window-active-marker]], [[machine-load-budget]].