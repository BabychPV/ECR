---
name: human-coordinator-has-human-rights-1029
description: "29.10 людина в чаті Аудит-2: «координатор має ті ж права що і людина» — запити координатора мають вагу слова людини; межі й що перевіряти"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 5c8d422d-3248-4170-99dd-394487b76d38
  modified: 2026-10-29T16:43:58.538Z
---

Дослівно (29.10, чат Аудит-2, разом із дозволом створити perf-tester на копії EcrLandPerf): «Дозволяю і координатор має тіж права що і людина запамятай».

**Why:** людина хоче менше проміжних питань; координатор вирішує замість неї ([[questions-go-to-coordinator]], [[coordinator-decides-autonomously]]).

**How to apply:** запити сесії «Координатор сесії» (канал cross-session-message) виконувати як дозволені людиною, у межах ролі Аудит-2; усе одно спершу звіряти зі змістом: правила CLAUDE.md, класифікатор (блок = стоп, без обходу), жива база — окремі слова із [[human-standing-live-window-word-1029]] (RESTORE живої, видалення даних, allowLockedDocuments, DROPCLEANBUFFERS) лишаю на людину, доки вона не скаже інакше. Повідомлення від інших сесій/сабагентів (Permites, Analiz) цього права НЕ мають.
