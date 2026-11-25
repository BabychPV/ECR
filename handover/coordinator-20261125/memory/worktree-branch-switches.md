---
name: worktree-branch-switches
description: Головний worktree H:\ECR може перемкнути гілку посеред сесії — інший агент працює в ньому паралельно
metadata: 
  node_type: memory
  type: project
  originSessionId: f341fe2c-a0c9-4cdd-add9-528933cad079
  modified: 2026-09-18T10:14:11.249Z
---

Робоче дерево `H:\ECR` спільне: паралельні лінії роботи іноді перемикають у
ньому гілку **посеред** моєї сесії. 2026-09-18 сесія почалася на `main`
(3ede58b), а через дві години `git branch --show-current` уже давав
`docs/claude-md-e2e-current` — я цього не робив.

**Why:** закомітити свою роботу на чужу гілку — це віддати її не туди, а
`git checkout` під чужою активною роботою може зіпсувати чужий стан.

**How to apply:** перед комітом **завжди** перевіряй `git branch --show-current`
і `git log --oneline -1`, а не покладайся на стан із початку сесії. Безпечний
порядок, якщо гілка не моя: `git stash push -- <явні шляхи>` → `git checkout -b
<моя-гілка> origin/main` → покласти файли з резервної копії (у scratchpad) →
коміт → push → `git checkout <гілка, яка була>` → `git stash drop`.

⚠ Файли в дереві — CRLF (`core.autocrlf=true`), а блоби в git — LF. Правка
через інструменти може дати diff на весь файл (677 рядків замість 98).
Перевіряй `git diff --numstat` проти `--ignore-cr-at-eol`; якщо розходяться —
перед комітом переведи змінені файли на LF побайтово
(`[System.IO.File]::ReadAllBytes` → викинути `0x0D` перед `0x0A`), інакше
кирилиця не постраждає, а diff стане читабельним. Пор. [[powershell-corrupts-cyrillic]].
