---
name: honesty-guard-forbids-skip
description: "honesty-guard (.github/guards/honesty-guard.sh) забороняє будь-який `Skip =`/`[Ignore` у тестах; не відкладай червоний тест через Skip"
metadata:
  node_type: memory
  type: feedback
  originSessionId: c16aeaa2-e44e-49f8-bcd8-d2d38e467e8e
  modified: 2026-10-07T07:40:55.284Z
---

Я наказала сабагенту лишити `[Fact(Skip = "TODO-R8…")]` для випадку, що чекає на іншу lane, — Evidence/honesty-guard на lane став червоним (07.10, sec-hidden-guards 89abf306 → виправлено 56a27577).

**Why:** гейт забороняє будь-який Skip/Ignore у diff; це свідомий захист від «зелених» тестів, які нічого не перевіряють.

**How to apply:** тест, що чекає на іншу lane, НЕ комітити зі Skip — прибрати випадок із цієї lane і повернути окремим [TEST]-комітом після зведення залежності (або тримати в тій самій lane, що містить залежність). Записати в листі готовності. Пов'язано: [[security-mutation-blocked-prove-red-before-fix]].
