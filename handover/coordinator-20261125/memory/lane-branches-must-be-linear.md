---
name: lane-branches-must-be-linear
description: "Інтегратор «Аудит» зводить lane-гілки cherry-pick'ом — merge-коміт у lane-гілці блокує зведення"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 4f26388c-991f-4666-b4f8-5a47388600fc
  modified: 2026-09-28T18:56:20.244Z
---

Lane-гілка для інтегратора має бути лінійною: лише свої коміти поверх вершини dev/integration, без merge-комітів. «Аудит» зводить cherry-pick'ом; merge-коміт cherry-pick не бере, а rebase губить ручні розв'язання — таку гілку він не зводить (2026-09-28, lane/sec/p8-apply-1 з merge 61704b4a).

**Why:** stacked-гілку на чужій запушеній гілці я оновив `git merge origin/dev/integration`, і вона стала незвідною.

**How to apply:** щоб оновити stacked-гілку — нова гілка (`…-2`) від свіжої вершини dev/integration + cherry-pick лише власних комітів; уже влиті коміти відсіє `git cherry`. Див. [[rebase-drops-merge-commits]], [[integrator-audit-lanes]].
