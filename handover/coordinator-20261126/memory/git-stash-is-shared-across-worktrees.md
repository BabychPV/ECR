---
name: git-stash-is-shared-across-worktrees
description: список git stash один на всі worktree; stash/pop паралельних агентів і сесій міняються записами
metadata: 
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-09-21T05:19:15.782Z
---

`git stash` у H:\ECR небезпечний: список стешів (`refs/stash`) спільний для ВСІХ worktree, а їх тут десятки й паралельно працюють кілька сесій.

2026-09-21 мій агент-інтегратор зробив `stash` → `dotnet test` → `stash pop`. Тим часом сесія «Головний» сховала свій стеш у своєму worktree. У результаті `pop` застосував ЧУЖІ 9 файлів (ErrorCodes.cs, 09-seed.sql…) у дерево агента. Спроби відкотити класифікатор дозволів заблокував («Interfere With Workloads»). Нічого не втратили лише тому, що власник уже забрав свій запис сам.

**Why:** доказ «червоне без зміни» агенти звикли робити через stash, а в спільному репозиторії це гонка.

**How to apply:**
- У кожному промпті агента: ⛔ `git stash`.
- Доказ «до/після» роблять на окремому `git worktree add --detach <шлях> <коміт>`.
- Якщо стеш усе ж змішався — спершу `git stash list` і `git -C <worktree власника> status`: з'ясувати, чия копія оригінал. Лише тоді чистити, і не силоміць.

Див. [[parallel-agents-same-defect]], [[audit-subagents-need-clean-tree-and-recheck]].
