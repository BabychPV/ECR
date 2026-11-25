---
name: work-queue-cycle
description: З 01.10 єдина черга робіт WORK-QUEUE.md + TODO-REMAINING + QUESTIONS-BUSINESS (відповіді людини = вимоги); сесії беруть верхню todo без дозволу
metadata:
  node_type: memory
  type: project
  originSessionId: cb9a6802-7e74-4b2a-a2e8-5d1277869b04
  modified: 2026-10-01T16:08:48.341Z
---

01.10 людина вписала відповіді в QUESTIONS-BUSINESS (файл у `.sync-local/reports/`) і просить, щоб усі сесії знали TODO-REMAINING, QUESTIONS-BUSINESS і WORK-QUEUE та щоб вони були вбудовані в розробку («поквапся»). Єдине джерело задач: `H:\ECR\.sync-local\WORK-QUEUE.md` (копія в repo `docs/build/WORK-QUEUE.md` — вносить «Аудит», разом із правилом у CLAUDE.md).

**Why:** розробка йшла від листа до листа; людина хоче план і швидкість. Відповіді людини в тому файлі — рішення (напр. Actual-календар, повна звірка EventFrame за період, SMTP+правила в нашій системі, `_Day` беремо всі чотири з дедуплікацією, Rollup/Check дефолт, allowlist порожній, ролі без іменних осіб).

**How to apply:** після перезапуску читати WORK-QUEUE.md; сесія, звільнившись, бере верхню todo свого списку без дозволу; нове питання до людини — лише через координатора і лише після спроби відповісти з docs/source (мій агент це вміє: Python-потокове читання, не ripgrep). Пов'язано: [[questions-go-to-coordinator]], [[coordinator-mandate]], [[direct-push-since-0929]].
