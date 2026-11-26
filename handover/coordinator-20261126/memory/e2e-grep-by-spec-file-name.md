---
name: e2e-grep-by-spec-file-name
description: "e2e-stand grep must use spec FILE names, not title words — serial describes break when setup tests are filtered out"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-13T10:05:55.849Z
---

For `gh workflow run e2e-stand.yml -f grep=...` use spec file base names (e.g. `methodologyCategories|tableChat`), never title words like «screenshots»/«security»/«axe».

**Why:** 12.11 aa-l29: grep words «screenshots|security» matched only the axe tests of `mode: 'serial'` describes in methodologyCategories.spec.ts and tableChat.spec.ts; their setup tests (create category, assign chatTable) were filtered out → 2 false reds, an extra e2e run and an investigation.

✎ 13.11 B76: the file name goes WITHOUT a directory — `e2e/flertSync` gives «No tests found» (a red run that is not a test result). To repeat one spec N times in parallel, vary the grep text (`flertSync`, `lertSync`, `flertSync.spec`); otherwise the concurrency group (it includes grep) cancels the duplicates.

**How to apply:** build the grep from the spec files covering touched screens; if a word may match titles in unrelated serial specs, drop it. Related: [[smoke-and-e2e]].
