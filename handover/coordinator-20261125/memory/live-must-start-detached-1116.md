---
name: live-must-start-detached-1116
description: Live EcrLandUi dies with a Claude desktop restart unless it is started detached via WMI; crash-restart recipe
metadata:
  node_type: memory
  type: project
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-16T06:18:20.715Z
---

16.11 04:12Z live (:5094/:6094) went down: Api/web had been started from a session shell, so the desktop app restart killed them with its process tree.

**Why:** Start-Process children stay in the session's job object. Only a process created through WMI (`Invoke-CimMethod Win32_Process Create`, parent WmiPrvSE) survives.

**How to apply:** start live (window run or restart) only through a WMI-created wrapper, e.g. C:\EcrScratch\w15v\restart-detached-1116.ps1 (sets W15V_MAIN_SHA, calls restart-w15v.ps1 … -CrashRestart). `-CrashRestart` tolerates a stale Running job row after a kill (the app marks it Failed at start); never use it in a window. See [[live-restart-w15u-hse400-flag-1114]], [[w15v-window-1114]].
