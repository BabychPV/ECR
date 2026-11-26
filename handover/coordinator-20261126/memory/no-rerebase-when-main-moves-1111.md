---
name: no-rerebase-when-main-moves-1111
description: "Coordinator 11.11 — don't re-rebase a green lane just because main moved; check merge-tree, re-rebase only on conflict or duplicate commits"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-11T21:00:00.123Z
---

Don't re-rebase a lane only because `origin/main` moved. If `git merge-tree --write-tree origin/main <tip>` is clean and the lane carries no foreign commits, the coordinator cuts the RC from the green SHA as is. Re-rebase only on a conflict or duplicate commits (e.g. RI1-B carrying D-474 commits that main already got via #812).

**Why:** 11.11 I rebased four lanes twice in an hour (-rb, then -rb2) as main took #812/#816/#815 — every rebase = new branch name (no force) + a full CI wasted.

**How to apply:** before rebasing, run merge-tree and `git log origin/main..<tip>` for foreign/duplicate commits; otherwise leave the lane and report the green SHA. Related: [[speed-rules-ci-process-1012]], [[no-force-lease-in-agent-prompts]].
