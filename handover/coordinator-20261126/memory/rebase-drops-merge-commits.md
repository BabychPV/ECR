---
name: rebase-drops-merge-commits
description: git rebase гілки з ручним merge-комітом мовчки викидає його (з кодом зведення); такі гілки зводити лише git merge
metadata: 
  node_type: memory
  type: feedback
  originSessionId: 7a8e1359-5171-48b8-a08d-8b2542a7a9f1
  modified: 2026-09-22T04:39:59.800Z
---

2026-09-22: на парі `feat/rule-coverage` (сервер + клієнт, з ручним розв'язанням конфлікту в `ErrorCodes.cs`) `git rebase` відкинув цілий merge-коміт із клієнтським кодом; відновили з reflog.

**Why:** rebase за замовчуванням лінеаризує історію й пропускає merge-коміти — разом із розв'язанням конфлікту, яке жило тільки в них.

**How to apply:** для гілок, де вже є merge-коміт (пари сервер+клієнт, гілки з `git merge origin/dev/integration`), в промптах агентів писати «зводити лише `git merge`, не rebase». Rebase лишається для лінійних непушених комітів поверх `origin/dev/integration`. Пов'язане: [[stacked-pr-base-delete]], [[merge-cadence]].
