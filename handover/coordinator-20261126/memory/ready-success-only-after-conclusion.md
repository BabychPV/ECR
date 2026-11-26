---
name: ready-success-only-after-conclusion
description: Never pre-write «CI … success» in a READY file; write it only after gh run view shows conclusion=success
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-11-12T16:06:19.472Z
---

Write «success» for a CI/Evidence run in `.sync-local\ready\*.md` only after `gh run view <id> --json status,conclusion` returns completed/success for the exact SHA. Do not draft the file in advance with «success» filled in.

**Why:** 12.11 aa-p4b: I drafted READY with «CI 43243242946 success» while the run still had 12 jobs queued. The coordinator auto-cuts RCs from READY files, so it read the draft as a verdict and flagged it.

**How to apply:** draft READY with «CI <id> — pending», then fill in after the verdict; the coordinator reads READY files directly. Related: [[verify-ci-not-local-subagent-claims]], [[full-ci-on-final-tip-1110]].
