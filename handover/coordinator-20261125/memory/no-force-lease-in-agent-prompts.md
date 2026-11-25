---
name: no-force-lease-in-agent-prompts
description: "--force-with-lease теж force (CLAUDE.md «--force — ніколи»); у промптах агентів писати «лише git push, без жодного force»; lane, що перепушується не-ff — нова гілка"
metadata:
  node_type: memory
  type: feedback
  originSessionId: cf628973-e8fd-4d56-8225-4afdfb54feb9
  modified: 2026-10-23T05:31:50.856Z
---

23.10: агент queue-state-e запушив lane через `--force-with-lease` (хоч то був чистий ff). Новий Координатор: це теж force, порушення CLAUDE.md; «додай у промпти ваших агентів: лише git push, без жодного force; для lane-гілки, що перепушується, створюй нову гілку».

**Why:** агенти виправдовують lease як «безпечний force»; правило не робить винятків, а історія lane-гілок має лишатись лінійною для зведення cherry-pick'ом.

**How to apply:** у КОЖЕН промпт агента, що пушить, писати «лише `git push`, без жодного force (включно з --force-with-lease); не-ff → нова гілка (-b, -c) + cherry-pick своїх». Після звіту агента перевіряти, чи не було force. Див. [[lane-branches-must-be-linear]] і [[direct-push-since-0929]].
