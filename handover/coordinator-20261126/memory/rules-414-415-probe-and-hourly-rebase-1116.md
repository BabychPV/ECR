---
name: rules-414-415-probe-and-hourly-rebase-1116
description: "Retro 16.11: race/double-action defects need a real probe; long lanes rebase onto main hourly"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-16T16:48:26.324Z
---

Rules 414/415 (coordinator retro, 16.11, for all sessions):

- **414:** for race / double-action defects a static verdict «closed by reading the code» is not accepted. Only a real probe counts: a concurrent test or a probe in the client.
- **415:** a lane that lives longer than 3 h while `main` is active rebases onto `origin/main` HOURLY while the code is still moving, so the final rebase is small.

**Why:**
- 414: B149 found 14 open dialogs among 60 that had been «static-closed».
- 415: the B141 pentest lane lost ~2 h to one final rebase over 60 commits (B139/B143 had touched RowLockHandlers).

**How to apply:**
- Put both rules in every subagent brief for long or concurrency work.
- 415 rebases only unpushed commits on a clean tree. Check the exit code before going on.
- After each hourly rebase, rerun the tests of the touched classes.

Related: [[rules-405-406-one-sha-own-architecture-run-1116]], [[rule-401-mutate-every-branch-before-ready-1116]]
