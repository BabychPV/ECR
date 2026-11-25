---
name: full-ci-on-final-tip-1110
description: READY needs FULL CI on the exact final tip; a non-integ lane gets it only via workflow_dispatch
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-10T08:56:24.843Z
---

Before sending READY, the lane's exact final tip must have a green FULL CI. RC gates (server/client/a11y/windows) read only that SHA. If the lane is not named `lane/integ-*`, run `gh workflow run ci.yml --ref <lane>` after the last push. This applies even when the delta since the last full-green SHA is only a test or spec.

**Why:** 10.11, RC #796 (ФВ-8.14) was BLOCKED. The final tip f82a02663 (an e2e spec on top of the full-green 3dc675601) had only the fast lane CI. The coordinator had accepted «fast CI is enough» and then reversed it as their mistake.

**How to apply:** prefer naming lanes `lane/integ-…` so a push runs the full set. Otherwise, after the final push, dispatch full ci.yml and wait for it. Pushing to a lane cancels its running CI (concurrency per ref), so batch commits before the full run. Related: [[speed-rules-ci-process-1012]].
