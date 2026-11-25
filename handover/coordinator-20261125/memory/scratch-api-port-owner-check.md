---
name: scratch-api-port-owner-check
description: "After starting a scratch API, check which process owns the port — an older API on the same port answers /health/live and the F13 runs on the wrong build"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-11-13T05:55:40.661Z
---

13.11 (#869 F13): two start scripts with different pid files shared :5198; the new build failed to bind, but the start script's /health/live check was answered by the OLD process (#867), and its «up: <version>» line reads the DLL on disk, not the server. My first #869 run tested the wrong build (sheetNameL10n missing → looked like a defect). Also a start-script guard regex `p2\d\d` silently refused to stop `p301`.

**Why:** a F13 verdict on the wrong build is worse than no verdict.
**How to apply:** after every scratch start run `Get-NetTCPConnection -LocalPort <port> -State Listen` and compare the owning process command line with the app dir you meant; one pid file per port; guard regexes `p\d{3}\w?`. Related: [[agent-killed-foreign-ecr-api]], [[verify-ci-not-local-subagent-claims]].
