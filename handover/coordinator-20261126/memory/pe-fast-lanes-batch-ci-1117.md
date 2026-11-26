---
name: pe-fast-lanes-batch-ci-1117
description: "P-0117-5 (людина 17.11 09:10Z): власні lane — швидкі lane/pe/<задача>; повний CI/F13/RC — лише на пачці integ-batch координатора"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-11-17T09:06:18.147Z
---

Людина 17.11 09:10Z: «важкі прогони — на пачках, де багато змін». Координатор: P-0117-5.

- Свої lane пушити як `lane/pe/<задача>` (НЕ integ-*): build + Architecture (git у PATH) + тести зачеплених класів + зачеплені e2e (grep).
- READY → SHA координатору; він cherry-pick кількох у `lane/integ-batch-<дата>-N` → ОДИН повний CI + Evidence, ОДИН F13 AU, ОДИН RC.
- 20× і мутації — лише для класу ризику (гонитви, безпека, дані). Після rebase, що міняє лише доки, чи байт-ідентичного контракту — не переганяти.
- Номери D-nnn — лише координатор (правило 431, «D-NEW» у READY); у шапці READY — перелік спільних файлів (правило 430).
- На початку кожного ходу читати `.sync-local\PRIORITY.md` (правило 428), «ACK <id>» першим рядком листа.

**Why:** години очікування повного CI на кожну lane; людина вимагає швидкості.
**How to apply:** не запускати повний CI на свою lane; не чекати його вердикту перед READY. Пов'язане: [[rules-405-406-one-sha-own-architecture-run-1116]].
