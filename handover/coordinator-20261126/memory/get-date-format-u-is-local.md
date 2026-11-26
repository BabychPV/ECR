---
name: get-date-format-u-is-local
description: "`Get-Date -Format u` prints LOCAL time with a «Z» suffix (machine is UTC+2); use (Get-Date).ToUniversalTime().ToString('u') for Z stamps"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-13T10:26:37.086Z
---

`Get-Date -Format u` does not convert to UTC: it appends «Z» to local time, and this machine is UTC+2.

**Why:** on 13.11 I told the coordinator «it's 12:25Z» when it was 10:25Z. The ETA I built on it was 2 h off, and the coordinator caught it. The coordinator's own stamps had drifted the same way earlier that day.

**How to apply:** for any time stamp written into letters, READY files or the coordinator's file, use `(Get-Date).ToUniversalTime().ToString('u')`.
