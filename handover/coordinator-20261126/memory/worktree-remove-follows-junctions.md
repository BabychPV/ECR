---
name: worktree-remove-follows-junctions
description: git worktree remove іде за NTFS-junction і спорожнює ціль — node_modules іншого worktree зникає
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-21T10:02:30.402Z
---

2026-09-21 агент підключив `node_modules` у тимчасовий detached-worktree через junction, щоб не копіювати. Потім `git worktree remove` (з `--force`) пройшов за junction і **спорожнив `node_modules` у робочому worktree** — того, на який junction вказував. Код не постраждав, але довелося заново робити `npm ci`.

**Why:** на Windows рекурсивне видалення йде в junction як у звичайну теку.

**How to apply:**
- для тимчасових worktree `node_modules` краще копіювати (`robocopy /E`) або ставити `npm ci`;
- якщо junction усе ж створено — спершу `cmd /c rmdir <шлях junction>` (прибирає лише посилання), і лише потім `git worktree remove`.

Див. [[git-stash-is-shared-across-worktrees]].
