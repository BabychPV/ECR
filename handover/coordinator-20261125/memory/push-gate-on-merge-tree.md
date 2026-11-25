---
name: push-gate-on-merge-tree
description: "Gate every lane push on `git merge-tree --write-tree origin/main <tip>` exit 0 in the same command; I once pushed a conflicting batch and burned a CI"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-13T03:41:19.025Z
---

Before `git push origin <tip>:refs/heads/lane/...`, run `git merge-tree --write-tree origin/main <tip>` and push ONLY if its exit code is 0. Put the check and the push in one command: `if ($LASTEXITCODE -eq 0) { git push ... }`.

**Why:** on 13.11 I printed «mt 1 7» (conflict in RecalculationService.cs) but the same command pushed pe-batch-3 anyway. I had to cancel its full CI and re-cut it as pe-batch-3b. Rule: one full CI per SHA.

**How to apply:** use the gated form in every push command. On a conflict, re-cut from fresh main with cherry-pick -x before pushing anything (see [[no-rerebase-when-main-moves-1111]]: re-cut only on conflict).
