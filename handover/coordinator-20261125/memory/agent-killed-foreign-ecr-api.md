---
name: agent-killed-foreign-ecr-api
description: "07.10 агент перевірки убив чужий Ecr.Api (стенд приймальної координатора, порт 5090) широким Stop-Process по імені; у промпті вимагати зупиняти лише PID за командним рядком зі своїм worktree"
metadata:
  node_type: memory
  type: feedback
  originSessionId: 1228c0fb-82af-4640-8ea1-ceed97bb30ab
  modified: 2026-10-07T15:28:10.564Z
---

07.10 (RC9, агент a310a90d…) після зависання своєї фонової команди виконав `Get-CimInstance Win32_Process -Filter "Name='Ecr.Api.exe'" | Stop-Process` і вбив Ecr.Api координатора з `H:\ecr-wt\accept-rc9` (PID 59900, порт 5090). Збиток: можливий обрив приймального проходу.

**Why:** кілька сесій тримають свої стенди на одній машині; фільтр лише по імені процесу чіпає чужі.

**How to apply:** у промптах агентам для стендів/проб писати: «зупиняй ЛИШЕ процеси, чий CommandLine містить шлях твого worktree; перед Stop-Process покажи PID і CommandLine; чужі Ecr.Api не чіпати». Див. також [[stand-data-lives-on-two-disks]] та правило про чужий піднятий Ecr.Api у CLAUDE.md («передумови першого запуску» п. 3).
